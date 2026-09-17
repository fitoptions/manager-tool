using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ManagerTool.Watchdog;

/// <summary>
/// Auto-update decision + trigger, owned by the watchdog service (LocalSystem).
///
/// The watchdog polls the server itself and, when this host is due, kicks off the update. It does
/// NOT run the install in its own process — the installer's first act is to stop ManagerToolSvc, so
/// an in-process install would kill the very process driving it (observed: it stuck at "installing").
/// Instead it launches update.ps1 as a DETACHED process (a separate powershell.exe, broken away via
/// cmd "start"), which survives the service being stopped and does the whole job — download, back up,
/// stop the agent, install, verify a live heartbeat, roll back on failure — reporting every stage.
///
/// This replaces the old broken chain (non-elevated agent -> schtasks -> ps1): the SYSTEM watchdog
/// launches the SAME proven script directly and reliably, with no scheduled-task permission hop.
/// It still cannot bypass Defender blocking an UNSIGNED installer — but we verified the silent
/// SYSTEM download runs clean, so signing is not required for this path.
/// </summary>
internal sealed class AgentUpdater
{
    private const string AppDir      = @"C:\Program Files\Manager Tool";
    private const string AgentExe    = AppDir + @"\ManagerTool.exe";
    private const string ConfigPath  = AppDir + @"\agent.config.json";
    private const string HealthPath  = @"C:\ProgramData\ManagerTool\health\alive.json";
    private const string MgmtDir     = @"C:\ProgramData\ManagerToolMgmt";
    private static readonly string UpdatePs1 = Path.Combine(MgmtDir, "update.ps1");
    private static readonly string LogPath   = Path.Combine(MgmtDir, "watchdog-update.log");

    private readonly string _svcVersion;
    private readonly HttpClient _http;
    private DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;

    public AgentUpdater(string serviceVersion)
    {
        _svcVersion = serviceVersion;
        _http = new HttpClient(new HttpClientHandler(), disposeHandler: true)
        { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"ManagerToolSvc/{_svcVersion} (+windows)");
    }

    /// <summary>One poll cycle. Best-effort: any failure is logged and retried next tick.</summary>
    public async Task CheckAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow < _cooldownUntil) return;
        try
        {
            var (serverUrl, enrollKey) = ReadConfig();
            if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(enrollKey)) return;

            var (curVer, hostId) = ReadAlive();
            if (string.IsNullOrEmpty(curVer)) curVer = FileVersion(AgentExe);
            if (string.IsNullOrEmpty(hostId) || string.IsNullOrEmpty(curVer)) return;

            var token = await EnrollAsync(serverUrl!, enrollKey!, hostId!, ct);
            if (token is null) return;

            var decision = await GetRolloutAsync(serverUrl!, token, curVer!, ct);
            if (decision is null || !decision.Value.Should) return;

            if (!File.Exists(UpdatePs1))
            {
                Log("update.ps1 missing - cannot update");
                await ReportAsync(serverUrl!, token, hostId!, "failed", decision.Value.Target,
                                  "updater script missing on host", ct);
                return;
            }

            Log($"due for {decision.Value.Target}; launching detached updater");
            await ReportAsync(serverUrl!, token, hostId!, "triggered", decision.Value.Target,
                              "watchdog launching updater", ct);
            LaunchDetachedUpdater();
            // Don't re-launch while it runs; the server also holds this host on an in-progress status.
            _cooldownUntil = DateTimeOffset.UtcNow.AddMinutes(15);
        }
        catch (Exception ex) { Log("check error: " + ex.Message); }
    }

    /// <summary>
    /// Launch update.ps1 as an INDEPENDENT process so the installer stopping ManagerToolSvc does not
    /// kill it. "cmd /c start" breaks the new powershell away from this service's process tree.
    /// </summary>
    private void LaunchDetachedUpdater()
    {
        try
        {
            var args = $"/c start \"MTUpdate\" /min powershell.exe -ExecutionPolicy Bypass " +
                       $"-WindowStyle Hidden -NonInteractive -File \"{UpdatePs1}\"";
            var psi = new ProcessStartInfo(Path.Combine(Env("SystemRoot"), @"System32\cmd.exe"), args)
            { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = MgmtDir };
            Process.Start(psi);
            Log("detached updater launched");
        }
        catch (Exception ex) { Log("launch failed: " + ex.Message); }
    }

    // ---- server calls ----

    private async Task<string?> EnrollAsync(string server, string key, string hostId, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { enrollmentKey = key, hostId, machineName = Environment.MachineName });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{server}/api/auth/enroll")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("accessToken", out var t) ? t.GetString() : null;
    }

    private async Task<(bool Should, string Target)?> GetRolloutAsync(
        string server, string token, string current, CancellationToken ct)
    {
        using var req = Auth(HttpMethod.Get,
            $"{server}/api/agent/rollout?current={Uri.EscapeDataString(current)}", token);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var should = root.TryGetProperty("shouldUpdate", out var s) && s.GetBoolean();
        var target = root.TryGetProperty("targetVersion", out var v) ? v.GetString() ?? "" : "";
        return (should, target);
    }

    private async Task ReportAsync(string server, string token, string hostId,
                                   string stage, string? version, string detail, CancellationToken ct)
    {
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                hostId, stage, version, detail,
                utc = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            });
            using var req = Auth(HttpMethod.Post, $"{server}/api/agent/update-status", token);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, ct);
            _ = resp;   // best-effort
        }
        catch (Exception ex) { Log("report failed: " + ex.Message); }
    }

    private static HttpRequestMessage Auth(HttpMethod method, string url, string token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    // ---- local helpers ----

    private static (string? ServerUrl, string? EnrollKey) ReadConfig()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            var r = doc.RootElement;
            var url = r.TryGetProperty("ServerUrl", out var u) ? u.GetString() : null;
            var key = r.TryGetProperty("EnrollmentKey", out var k) ? k.GetString() : null;
            return (url?.TrimEnd('/'), key);
        }
        catch { return (null, null); }
    }

    private static (string? Version, string? HostId) ReadAlive()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(HealthPath));
            var r = doc.RootElement;
            var v = r.TryGetProperty("version", out var ve) ? ve.GetString() : null;
            var h = r.TryGetProperty("hostId", out var he) ? he.GetString() : null;
            return (v, h);
        }
        catch { return (null, null); }
    }

    private static string? FileVersion(string exe)
    {
        try { return FileVersionInfo.GetVersionInfo(exe).ProductVersion?.Split('+')[0]; }
        catch { return null; }
    }

    private static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? @"C:\Windows";

    private static void Log(string m)
    {
        try
        {
            Directory.CreateDirectory(MgmtDir);
            File.AppendAllText(LogPath, $"{DateTimeOffset.UtcNow:o}  {m}{Environment.NewLine}");
        }
        catch { }
    }
}
