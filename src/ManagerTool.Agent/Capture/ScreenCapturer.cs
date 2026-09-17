using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using ManagerTool.Shared;
using WinForms = System.Windows.Forms;

namespace ManagerTool.Agent.Capture;

/// <summary>
/// On-demand tile-delta screen capturer. Each tick it grabs the selected monitor with a GDI
/// BitBlt, splits it into a fixed grid of tiles, and emits only the tiles whose pixels changed
/// since the previous tick. A mostly-static screen (a trading terminal between ticks) therefore
/// sends almost nothing, so updates arrive far sooner than a whole-screen JPEG would allow while
/// staying sharp. Runs ONLY while an admin is viewing — Start() on BeginScreenStream, Stop() on
/// EndScreenStream.
///
/// A <b>keyframe</b> (every tile) is emitted as the first frame of a stream, whenever the monitor
/// selection changes, when the server asks for one (a viewer just joined), and periodically as a
/// self-heal against any delta that never arrived. Everything else is a delta.
/// </summary>
public sealed class ScreenCapturer : IDisposable
{
    // Cap the streamed width. A trading screen is captured at native res (often 2560px+) but the
    // viewer never shows it that large, so downscaling here — before encoding — is a large cut to
    // the payload. Text stays readable at ~1366px; screens already smaller are untouched.
    private const int MaxStreamWidth = 1366;

    // Grid cell size. 256px balances two costs: smaller tiles isolate change better (a blinking
    // cursor dirties less area) but add per-tile JPEG header overhead; larger tiles waste bytes
    // resending unchanged pixels around a small change. 256 is a good middle for text-dense UIs.
    private const int TileSize = 256;

    // Force a full keyframe at least this often, so a viewer recovers within a few seconds even if
    // a delta was dropped somewhere in the relay.
    private static readonly TimeSpan KeyframeInterval = TimeSpan.FromSeconds(5);

    private readonly string _hostId;
    private readonly DispatcherTimer _timer;
    private readonly long _jpegQuality;
    private int _selectedMonitor;

    // Per-tile content hash from the previous tick; an index missing or differing means "changed".
    private ulong[] _hashes = Array.Empty<ulong>();
    private int _hashCols, _hashRows;
    private bool _forceKeyframe = true;
    private DateTimeOffset _lastKeyframeUtc = DateTimeOffset.MinValue;

    /// <summary>Raised with a keyframe or a delta whenever there is something to send.</summary>
    public event Action<ScreenTileFrame>? TilesCaptured;

    public ScreenCapturer(string hostId, double fps = 8, long jpegQuality = 72)
    {
        _hostId = hostId;
        _jpegQuality = jpegQuality;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0 / fps) };
        _timer.Tick += (_, _) => CaptureSelected();
    }

    public void Start()
    {
        _forceKeyframe = true;   // a fresh viewer must get a complete image first
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    /// <summary>Switch which monitor is streamed. A different display is a different image, so the
    /// next frame must be a keyframe and the old per-tile hashes are void.</summary>
    public void SetMonitor(int index)
    {
        var next = Math.Max(0, index);
        if (next == _selectedMonitor) return;
        _selectedMonitor = next;
        _hashes = Array.Empty<ulong>();
        _forceKeyframe = true;
    }

    /// <summary>Server asked for a fresh full frame (e.g. a viewer just joined an active stream).</summary>
    public void RequestKeyframe() => _forceKeyframe = true;

    private void CaptureSelected()
    {
        var screens = WinForms.Screen.AllScreens;
        var count = screens.Length;
        if (count == 0)
            return;

        var index = Math.Min(_selectedMonitor, count - 1);
        try
        {
            CaptureOne(screens[index], index, count);
        }
        catch
        {
            // Session locked / secure desktop (UAC) / display detached — skip this tick.
        }
    }

    private void CaptureOne(WinForms.Screen screen, int index, int count)
    {
        var bounds = screen.Bounds;

        using var full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(full))
            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);

        using var frame = Downscale(full);
        var width = frame.Width;
        var height = frame.Height;

        var cols = (width + TileSize - 1) / TileSize;
        var rows = (height + TileSize - 1) / TileSize;

        // Read the whole frame's pixels once into a managed buffer; hashing and per-tile encoding
        // both work from it, so the bitmap is locked exactly once per tick.
        var data = frame.LockBits(new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        byte[] buffer;
        int stride = Math.Abs(data.Stride);
        try
        {
            buffer = new byte[stride * height];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
        }
        finally
        {
            frame.UnlockBits(data);
        }

        var keyframe = _forceKeyframe
                       || _hashes.Length != cols * rows
                       || _hashCols != cols || _hashRows != rows
                       || DateTimeOffset.UtcNow - _lastKeyframeUtc >= KeyframeInterval;

        if (keyframe)
        {
            _hashes = new ulong[cols * rows];
            _hashCols = cols;
            _hashRows = rows;
        }

        var changed = new List<ScreenTile>();
        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < cols; col++)
            {
                var x = col * TileSize;
                var y = row * TileSize;
                var w = Math.Min(TileSize, width - x);
                var h = Math.Min(TileSize, height - y);
                var i = row * cols + col;

                var hash = HashTile(buffer, stride, x, y, w, h);
                if (!keyframe && hash == _hashes[i])
                    continue;

                _hashes[i] = hash;
                changed.Add(new ScreenTile(i, EncodeTile(buffer, stride, x, y, w, h, _jpegQuality)));
            }
        }

        if (keyframe)
            _lastKeyframeUtc = DateTimeOffset.UtcNow;

        // Nothing moved on a delta tick — send nothing; the viewer keeps its last image.
        if (changed.Count == 0 && !keyframe)
            return;

        _forceKeyframe = false;
        TilesCaptured?.Invoke(new ScreenTileFrame(
            _hostId, DateTimeOffset.UtcNow, index, count, width, height,
            TileSize, cols, rows, keyframe, changed.ToArray()));
    }

    /// <summary>Returns a width-capped copy of the frame, or the original when already within the cap.</summary>
    private static Bitmap Downscale(Bitmap source)
    {
        if (source.Width <= MaxStreamWidth)
            return (Bitmap)source.Clone();

        var scale = MaxStreamWidth / (double)source.Width;
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var target = new Bitmap(MaxStreamWidth, height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(target);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.DrawImage(source, 0, 0, MaxStreamWidth, height);
        return target;
    }

    /// <summary>FNV-1a 64-bit over a tile's raw BGR rows — fast, allocation-free change detection.</summary>
    private static ulong HashTile(byte[] buffer, int stride, int x, int y, int w, int h)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offset;
        var bytesPerRow = w * 3;
        for (var r = 0; r < h; r++)
        {
            var start = (y + r) * stride + x * 3;
            for (var b = 0; b < bytesPerRow; b++)
                hash = (hash ^ buffer[start + b]) * prime;
        }
        return hash;
    }

    /// <summary>Copies one tile's rows out of the frame buffer and JPEG-encodes it.</summary>
    private static byte[] EncodeTile(byte[] buffer, int stride, int x, int y, int w, int h, long quality)
    {
        using var tile = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        var dest = tile.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var bytesPerRow = w * 3;
            for (var r = 0; r < h; r++)
                Marshal.Copy(buffer, (y + r) * stride + x * 3,
                    dest.Scan0 + r * dest.Stride, bytesPerRow);
        }
        finally
        {
            tile.UnlockBits(dest);
        }
        return Encode(tile, quality);
    }

    private static byte[] Encode(Bitmap bmp, long quality)
    {
        var jpegCodec = ImageCodecInfo.GetImageEncoders()
            .First(c => c.FormatID == ImageFormat.Jpeg.Guid);

        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);

        using var ms = new MemoryStream();
        bmp.Save(ms, jpegCodec, parameters);
        return ms.ToArray();
    }

    public void Dispose() => _timer.Stop();
}
