using System.Text.Json;

namespace ManagerTool.Server;

/// <summary>Metadata for one recorded session, read from its folder + manifest.</summary>
public sealed record RecordingSession(
    string HostId,
    string SessionId,      // the {yyyyMMdd-HHmmss} folder name
    DateTimeOffset StartUtc,
    DateTimeOffset? EndUtc,
    long FrameCount,
    bool Complete);

/// <summary>
/// Read-only browser over the on-disk recordings tree ({root}/{hostId}/{sessionId}/). Lists
/// sessions and serves individual frames for the playback UI. All caller-supplied path parts
/// are sanitized to their file-name component to prevent directory traversal.
/// </summary>
public sealed class RecordingLibrary
{
    private readonly string _root;

    public RecordingLibrary(RecordingOptions options)
    {
        _root = Path.IsPathRooted(options.Directory)
            ? options.Directory
            : Path.Combine(AppContext.BaseDirectory, options.Directory);
    }

    public IReadOnlyList<RecordingSession> ListSessions(string? hostId = null)
    {
        var results = new List<RecordingSession>();
        if (!Directory.Exists(_root))
            return results;

        var hostDirs = hostId is null
            ? Directory.GetDirectories(_root)
            : new[] { Path.Combine(_root, Safe(hostId)) }.Where(Directory.Exists).ToArray();

        foreach (var hostDir in hostDirs)
        {
            var host = Path.GetFileName(hostDir);
            foreach (var sessionDir in Directory.GetDirectories(hostDir))
                results.Add(ReadSession(host, sessionDir));
        }
        // Newest first.
        return results.OrderByDescending(s => s.StartUtc).ToList();
    }

    private static RecordingSession ReadSession(string host, string sessionDir)
    {
        var sessionId = Path.GetFileName(sessionDir);
        var frameCount = Directory.EnumerateFiles(sessionDir, "*.jpg").Count();
        DateTimeOffset start = DateTimeOffset.MinValue;
        DateTimeOffset? endN = null;
        var complete = false;

        var manifestPath = Path.Combine(sessionDir, "manifest.json");
        if (File.Exists(manifestPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var r = doc.RootElement;
                if (r.TryGetProperty("startUtc", out var s)) start = s.GetDateTimeOffset();
                if (r.TryGetProperty("endUtc", out var e) && e.ValueKind != JsonValueKind.Null) endN = e.GetDateTimeOffset();
                if (r.TryGetProperty("complete", out var c)) complete = c.GetBoolean();
            }
            catch { /* fall back to folder-derived values */ }
        }
        if (start == DateTimeOffset.MinValue)
            start = Directory.GetCreationTimeUtc(sessionDir);

        return new RecordingSession(host, sessionId, start, endN, frameCount, complete);
    }

    /// <summary>Ordered frame file names for a session (e.g. frame-000001-mon0.jpg).</summary>
    public IReadOnlyList<string> ListFrames(string hostId, string sessionId)
    {
        var dir = Path.Combine(_root, Safe(hostId), Safe(sessionId));
        if (!Directory.Exists(dir))
            return Array.Empty<string>();
        return Directory.GetFiles(dir, "*.jpg")
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Absolute path to one frame, or null if it does not resolve inside the session.</summary>
    public string? FramePath(string hostId, string sessionId, string frameName)
    {
        var name = Path.GetFileName(frameName);   // strip any path parts
        if (string.IsNullOrEmpty(name) || !name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            return null;
        var path = Path.Combine(_root, Safe(hostId), Safe(sessionId), name);
        return File.Exists(path) ? path : null;
    }

    // Reduce a caller-supplied segment to a single safe path component.
    private static string Safe(string s) => Path.GetFileName(s);
}
