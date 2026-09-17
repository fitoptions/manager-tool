using System.IO;
using System.Text.Json;
using ManagerTool.Shared;

namespace ManagerTool.Agent;

/// <summary>
/// Durable, order-preserving, bounded buffer for activity events. Every event is
/// appended to a JSONL file under ProgramData so nothing is lost across an unreachable
/// server, an agent crash, or a reboot. On reconnect the agent drains the buffer in
/// order (oldest first). The buffer is capped to bound disk use — once full, the oldest
/// events are dropped (the live screen view and newer activity matter more than ancient
/// backlog).
///
/// Only activity is buffered. Screen frames are live-only and never queued.
/// </summary>
public sealed class ActivityBuffer
{
    private readonly string _path;
    private readonly int _maxEvents;
    private readonly object _gate = new();
    private readonly LinkedList<ActivityEvent> _pending = new();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public ActivityBuffer(string? path = null, int maxEvents = 50_000)
    {
        _path = path ?? DefaultPath();
        _maxEvents = maxEvents;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        Load();
    }

    private static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ManagerTool", "activity-buffer.jsonl");

    public int Count { get { lock (_gate) return _pending.Count; } }

    /// <summary>Persist a new event. Drops oldest events if the cap is exceeded.</summary>
    public void Append(ActivityEvent e)
    {
        lock (_gate)
        {
            _pending.AddLast(e);
            if (_pending.Count > _maxEvents)
            {
                while (_pending.Count > _maxEvents)
                    _pending.RemoveFirst();
                Rewrite();           // dropped oldest → compact the file
            }
            else
            {
                AppendLine(e);       // fast path: single append
            }
        }
    }

    /// <summary>Oldest-first snapshot of everything currently buffered.</summary>
    public List<ActivityEvent> Snapshot()
    {
        lock (_gate) return _pending.ToList();
    }

    /// <summary>Remove the first <paramref name="count"/> events after they were sent.</summary>
    public void RemoveFirst(int count)
    {
        lock (_gate)
        {
            for (var i = 0; i < count && _pending.Count > 0; i++)
                _pending.RemoveFirst();
            Rewrite();
        }
    }

    // ---- persistence ----

    private void Load()
    {
        if (!File.Exists(_path))
            return;
        try
        {
            foreach (var line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                var evt = JsonSerializer.Deserialize<ActivityEvent>(line, Json);
                if (evt is not null)
                    _pending.AddLast(evt);
            }
            // Enforce the cap in case the file predates a smaller limit.
            while (_pending.Count > _maxEvents)
                _pending.RemoveFirst();
        }
        catch
        {
            // Corrupt buffer file (partial write on power loss): start clean rather than crash.
            _pending.Clear();
        }
    }

    private void AppendLine(ActivityEvent e)
    {
        try
        {
            using var writer = new StreamWriter(_path, append: true);
            writer.WriteLine(JsonSerializer.Serialize(e, Json));
        }
        catch
        {
            // Disk full / locked: keep the in-memory copy; a later Rewrite may succeed.
        }
    }

    private void Rewrite()
    {
        try
        {
            var tmp = _path + ".tmp";
            using (var writer = new StreamWriter(tmp, append: false))
            {
                foreach (var e in _pending)
                    writer.WriteLine(JsonSerializer.Serialize(e, Json));
            }
            File.Copy(tmp, _path, overwrite: true);
            File.Delete(tmp);
        }
        catch
        {
            // Best-effort; in-memory list remains the source of truth this session.
        }
    }
}
