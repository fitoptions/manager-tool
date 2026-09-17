using System.Collections.Concurrent;
using System.Threading.Channels;
using ManagerTool.Shared;
using Microsoft.AspNetCore.SignalR;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ManagerTool.Server;

/// <summary>
/// Sits between the agent's raw frames and the viewers, doing two things the agent cannot
/// be updated to do right now:
///
/// 1. <b>Conflation.</b> Each host/monitor has a single-slot queue: a new frame replaces any
///    frame still waiting to go out. A slow viewer link therefore always receives the LATEST
///    frame instead of an ever-growing backlog — latency stays bounded at roughly one frame
///    instead of compounding.
///
/// 2. <b>Transcoding.</b> Frames arrive at the host's native resolution. Before fan-out they
///    are downscaled to a viewer-appropriate width and re-encoded, cutting the server→browser
///    payload several-fold while staying readable. The recorder still receives the original
///    full-resolution frame — only the live relay is reduced.
/// </summary>
public sealed class FrameRelay : IDisposable
{
    private const int DefaultMaxWidth = 1366;
    private const int DefaultJpegQuality = 60;

    private readonly IHubContext<MonitorHub> _hub;
    private readonly ILogger<FrameRelay> _log;
    private readonly int _maxWidth;
    private readonly int _jpegQuality;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, Channel<ScreenFrame>> _lanes = new();

    public FrameRelay(IHubContext<MonitorHub> hub, IConfiguration config, ILogger<FrameRelay> log)
    {
        _hub = hub;
        _log = log;
        _maxWidth = config.GetValue("ManagerTool:Stream:MaxWidth", DefaultMaxWidth);
        _jpegQuality = config.GetValue("ManagerTool:Stream:JpegQuality", DefaultJpegQuality);
    }

    /// <summary>Queue a frame for delivery; if the previous one has not gone out yet, it is replaced.</summary>
    public void Enqueue(ScreenFrame frame)
    {
        var lane = _lanes.GetOrAdd(LaneKey(frame), _ => StartLane());
        lane.Writer.TryWrite(frame);   // capacity 1, DropOldest — never blocks, never backs up
    }

    private static string LaneKey(ScreenFrame f) => $"{f.HostId}#{f.MonitorIndex}";

    private Channel<ScreenFrame> StartLane()
    {
        var lane = Channel.CreateBounded<ScreenFrame>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        _ = Task.Run(() => PumpAsync(lane.Reader, _cts.Token));
        return lane;
    }

    private async Task PumpAsync(ChannelReader<ScreenFrame> reader, CancellationToken ct)
    {
        await foreach (var frame in reader.ReadAllAsync(ct))
        {
            try
            {
                var outgoing = Transcode(frame);
                await _hub.Clients.Group(HubRoutes.ViewerGroup(frame.HostId))
                    .SendAsync(nameof(IAdminClient.FrameReceived), outgoing, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // One bad frame (corrupt JPEG, transient hub fault) must not kill the lane.
                _log.LogWarning(ex, "Frame relay failed for host {HostId}", frame.HostId);
            }
        }
    }

    private ScreenFrame Transcode(ScreenFrame frame)
    {
        if (frame.Width <= _maxWidth)
            return frame;   // already small enough — re-encoding would only cost quality

        using var image = Image.Load(frame.JpegBytes);
        var scaledHeight = (int)Math.Round(image.Height * (_maxWidth / (double)image.Width));
        image.Mutate(x => x.Resize(_maxWidth, scaledHeight));

        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms, new JpegEncoder { Quality = _jpegQuality });
        return frame with { Width = _maxWidth, Height = scaledHeight, JpegBytes = ms.ToArray() };
    }

    // ============================ Tile-delta relay (agents >= 1.1) ============================
    //
    // Full frames above are conflated by dropping the older one — losing an intermediate whole
    // screen is harmless. A tile DELTA cannot be dropped that way: its changed cells would be lost
    // until the next keyframe. So each host keeps a lane that MERGES pending deltas (latest bytes
    // win per cell) and holds the newest tile of every cell, so a viewer joining mid-stream can be
    // handed a complete keyframe and a slow link never accumulates a backlog.

    private readonly ConcurrentDictionary<string, TileLane> _tileLanes = new();

    /// <summary>Ingest a tile frame: update the per-cell cache, merge into the pending set, wake the pump.</summary>
    public void EnqueueTiles(ScreenTileFrame frame, Recorder recorder)
    {
        var lane = _tileLanes.GetOrAdd(frame.HostId, id => StartTileLane(id, recorder));
        lane.Ingest(frame);
    }

    /// <summary>Assemble a full keyframe from the cached tiles for a viewer who just joined.</summary>
    public bool TryBuildKeyframe(string hostId, out ScreenTileFrame keyframe)
    {
        if (_tileLanes.TryGetValue(hostId, out var lane) && lane.TryBuildKeyframe(out keyframe!))
            return true;
        keyframe = null!;
        return false;
    }

    /// <summary>Discard a host's cached tiles — the streamed image is about to change (monitor switch).</summary>
    public void InvalidateCache(string hostId)
    {
        if (_tileLanes.TryGetValue(hostId, out var lane))
            lane.Invalidate();
    }

    private TileLane StartTileLane(string hostId, Recorder recorder)
    {
        var lane = new TileLane(hostId);
        _ = Task.Run(() => PumpTilesAsync(lane, recorder, _cts.Token));
        return lane;
    }

    private async Task PumpTilesAsync(TileLane lane, Recorder recorder, CancellationToken ct)
    {
        await foreach (var _ in lane.Wakeups.Reader.ReadAllAsync(ct))
        {
            ScreenTileFrame? outgoing = lane.DrainPending();
            if (outgoing is null)
                continue;

            try
            {
                await _hub.Clients.Group(HubRoutes.ViewerGroup(lane.HostId))
                    .SendAsync(nameof(IAdminClient.TilesReceived), outgoing, ct);

                // Recording needs whole frames on disk. Composite the cached cells only while a
                // recording is actually running — otherwise this cost is never paid.
                if (recorder.IsRecording(lane.HostId) && lane.TryBuildKeyframe(out var full))
                    recorder.MaybeWrite(CompositeToFrame(full));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Tile relay failed for host {HostId}", lane.HostId);
            }
        }
    }

    /// <summary>Paint the tiles of a keyframe onto one image and JPEG-encode it, for the recorder.</summary>
    private static ScreenFrame CompositeToFrame(ScreenTileFrame kf)
    {
        using var canvas = new Image<Rgb24>(kf.FrameWidth, kf.FrameHeight);
        foreach (var tile in kf.Tiles)
        {
            var col = tile.Index % kf.Cols;
            var row = tile.Index / kf.Cols;
            using var piece = Image.Load<Rgb24>(tile.JpegBytes);
            canvas.Mutate(c => c.DrawImage(piece, new Point(col * kf.TileSize, row * kf.TileSize), 1f));
        }
        using var ms = new MemoryStream();
        canvas.SaveAsJpeg(ms, new JpegEncoder { Quality = 80 });
        return new ScreenFrame(kf.HostId, kf.TimestampUtc, kf.FrameWidth, kf.FrameHeight,
            ms.ToArray(), kf.MonitorIndex, kf.MonitorCount);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}

/// <summary>
/// One host's tile state: the newest bytes of every cell (for building a keyframe on demand) plus
/// the cells changed since the last flush (for the next delta). All mutation is under one lock; the
/// pump is woken through a single-slot channel so bursts coalesce into one send.
/// </summary>
internal sealed class TileLane
{
    private readonly object _gate = new();
    private readonly Dictionary<int, byte[]> _latest = new();   // every cell's newest bytes
    private readonly Dictionary<int, byte[]> _pending = new();  // cells awaiting the next flush
    private bool _pendingIsKeyframe;

    // Geometry of the most recent frame; a keyframe we build must describe the current grid.
    private int _monitorIndex, _monitorCount, _frameWidth, _frameHeight, _tileSize, _cols, _rows;
    private bool _haveGeometry;

    public string HostId { get; }
    public Channel<bool> Wakeups { get; } =
        Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public TileLane(string hostId) => HostId = hostId;

    public void Ingest(ScreenTileFrame frame)
    {
        lock (_gate)
        {
            _monitorIndex = frame.MonitorIndex;
            _monitorCount = frame.MonitorCount;
            _frameWidth = frame.FrameWidth;
            _frameHeight = frame.FrameHeight;
            _tileSize = frame.TileSize;
            _cols = frame.Cols;
            _rows = frame.Rows;
            _haveGeometry = true;

            if (frame.Keyframe)
            {
                // A keyframe supersedes everything cached: reset both maps to it. A delta that
                // arrives before the pump runs will merge on top and the flush stays a full frame.
                _latest.Clear();
                _pending.Clear();
                _pendingIsKeyframe = true;
            }

            foreach (var tile in frame.Tiles)
            {
                _latest[tile.Index] = tile.JpegBytes;
                _pending[tile.Index] = tile.JpegBytes;
            }
        }
        Wakeups.Writer.TryWrite(true);
    }

    /// <summary>Take everything pending as one frame, or null if nothing is waiting.</summary>
    public ScreenTileFrame? DrainPending()
    {
        lock (_gate)
        {
            if (_pending.Count == 0)
                return null;

            var tiles = _pending.Select(kv => new ScreenTile(kv.Key, kv.Value)).ToArray();
            var isKeyframe = _pendingIsKeyframe;
            _pending.Clear();
            _pendingIsKeyframe = false;

            return new ScreenTileFrame(HostId, DateTimeOffset.UtcNow, _monitorIndex, _monitorCount,
                _frameWidth, _frameHeight, _tileSize, _cols, _rows, isKeyframe, tiles);
        }
    }

    /// <summary>Build a complete keyframe from the cached cells (for a late joiner or the recorder).</summary>
    public bool TryBuildKeyframe(out ScreenTileFrame keyframe)
    {
        lock (_gate)
        {
            if (!_haveGeometry || _latest.Count == 0)
            {
                keyframe = null!;
                return false;
            }
            var tiles = _latest.Select(kv => new ScreenTile(kv.Key, kv.Value)).ToArray();
            keyframe = new ScreenTileFrame(HostId, DateTimeOffset.UtcNow, _monitorIndex, _monitorCount,
                _frameWidth, _frameHeight, _tileSize, _cols, _rows, true, tiles);
            return true;
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _latest.Clear();
            _pending.Clear();
            _pendingIsKeyframe = false;
            _haveGeometry = false;
        }
    }
}
