using System.IO;
using System.Text.Json;

namespace ManagerTool.Agent;

/// <summary>
/// Writes a small "I am alive and connected" file that the SYSTEM update task reads to decide
/// whether a freshly-installed agent is actually working before the old one is discarded.
///
/// The file lives in a fixed, machine-wide spot (<c>%ProgramData%\ManagerTool\health\alive.json</c>)
/// that the non-elevated agent can write and the SYSTEM updater can read. It records the running
/// agent's version and the moment it last confirmed a live server connection. The updater treats a
/// heartbeat as healthy only when it names the NEW version AND is fresh — so an old agent that never
/// went down, or a new one that starts but cannot reach the server, both correctly read as "not yet
/// healthy" and trigger a rollback.
///
/// Best-effort throughout: a failed write must never disturb monitoring, so every path swallows IO
/// errors. The updater's own timeout covers the case where nothing is ever written.
/// </summary>
internal sealed class HealthBeacon
{
    private readonly string _path;
    private readonly string _version;
    private readonly string _hostId;

    public HealthBeacon(string version, string hostId)
    {
        _version = version;
        _hostId = hostId;
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ManagerTool", "health");
        _path = Path.Combine(dir, "alive.json");
        TryEnsureDir(dir);
    }

    /// <summary>Record that the agent is connected to the server right now.</summary>
    public void Beat() => Write(connected: true);

    private void Write(bool connected)
    {
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                version = _version,
                hostId = _hostId,   // the SYSTEM updater reads this to enrol + report as the RIGHT host
                connected,
                utc = DateTimeOffset.UtcNow.ToString("o"),
            });
            // Write-then-rename so the updater never reads a half-written file.
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // Best-effort: monitoring must not depend on this file being writable.
        }
    }

    private static void TryEnsureDir(string dir)
    {
        try { Directory.CreateDirectory(dir); }
        catch { /* installer normally pre-creates it with the right ACL; ignore if we cannot */ }
    }
}
