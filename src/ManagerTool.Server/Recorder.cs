using System.Collections.Concurrent;
using System.Text.Json;
using ManagerTool.Shared;

namespace ManagerTool.Server;

/// <summary>Strongly-typed view of the "ManagerTool:Recording" config section.</summary>
public sealed class RecordingOptions
{
    /// <summary>Root folder for recorded sessions. Relative paths resolve under the app base.</summary>
    public string Directory { get; set; } = "recordings";

    /// <summary>How long recorded sessions are kept. 0 or negative disables purging.</summary>
    public int RetentionDays { get; set; } = 30;
}

/// <summary>
/// Persists live frames to disk for hosts that an admin has explicitly put into recording.
/// Each recording is a folder: {root}/{hostId}/{startUtc}/ containing sequential JPEGs
/// (frame-000001-mon0.jpg) plus a manifest.json. Nothing is written unless recording is on.
/// </summary>
public sealed class Recorder
{
    private readonly string _root;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    public Recorder(RecordingOptions options)
    {
        _root = Path.IsPathRooted(options.Directory)
            ? options.Directory
            : Path.Combine(AppContext.BaseDirectory, options.Directory);
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    public bool IsRecording(string hostId) => _sessions.ContainsKey(hostId);

    /// <returns>true if a new session was started (false if already recording).</returns>
    public bool Start(string hostId, string startedBy)
    {
        // Foldername uses a fixed, filesystem-safe UTC stamp captured now.
        var startUtc = DateTimeOffset.UtcNow;
        var folderStamp = startUtc.ToString("yyyyMMdd-HHmmss");
        var dir = Path.Combine(_root, Sanitize(hostId), folderStamp);

        var session = new Session(dir, startUtc, startedBy);
        if (!_sessions.TryAdd(hostId, session))
            return false;

        Directory.CreateDirectory(dir);
        session.WriteManifest(hostId);
        return true;
    }

    /// <returns>true if a session was stopped.</returns>
    public bool Stop(string hostId)
    {
        if (!_sessions.TryRemove(hostId, out var session))
            return false;
        session.Finalize(hostId);
        return true;
    }

    /// <summary>Write a frame if the host is recording; no-op otherwise.</summary>
    public void MaybeWrite(ScreenFrame frame)
    {
        if (_sessions.TryGetValue(frame.HostId, out var session))
            session.Write(frame);
    }

    private static string Sanitize(string s) =>
        string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private sealed class Session
    {
        private readonly string _dir;
        private readonly DateTimeOffset _startUtc;
        private readonly string _startedBy;
        private readonly object _gate = new();
        private long _sequence;
        private DateTimeOffset _lastFrameUtc;

        public Session(string dir, DateTimeOffset startUtc, string startedBy)
        {
            _dir = dir;
            _startUtc = startUtc;
            _startedBy = startedBy;
            _lastFrameUtc = startUtc;
        }

        public void Write(ScreenFrame frame)
        {
            long seq;
            lock (_gate)
            {
                seq = ++_sequence;
                _lastFrameUtc = frame.TimestampUtc;
            }

            var name = $"frame-{seq:D6}-mon{frame.MonitorIndex}.jpg";
            try
            {
                File.WriteAllBytes(Path.Combine(_dir, name), frame.JpegBytes);
            }
            catch
            {
                // Disk full / permissions: drop this frame rather than kill the relay.
            }
        }

        public void WriteManifest(string hostId) => WriteManifest(hostId, isFinal: false);

        public void Finalize(string hostId) => WriteManifest(hostId, isFinal: true);

        private void WriteManifest(string hostId, bool isFinal)
        {
            long frames;
            DateTimeOffset last;
            lock (_gate) { frames = _sequence; last = _lastFrameUtc; }

            var manifest = new
            {
                hostId,
                startedBy = _startedBy,
                startUtc = _startUtc,
                endUtc = isFinal ? last : (DateTimeOffset?)null,
                frameCount = frames,
                complete = isFinal,
            };

            try
            {
                File.WriteAllText(
                    Path.Combine(_dir, "manifest.json"),
                    JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* best-effort */ }
        }
    }
}
