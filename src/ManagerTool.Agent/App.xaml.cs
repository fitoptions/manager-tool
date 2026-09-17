using System.IO;
using System.Text.Json;
using System.Windows;
using ManagerTool.Shared;

namespace ManagerTool.Agent;

public partial class App : System.Windows.Application
{
    private AgentConnection? _connection;
    private Mutex? _instanceGate;

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        // Single-instance guard: the agent can be launched by BOTH the logon Run key and the
        // watchdog service. Only the first should run; a second instance in the same session
        // exits immediately so we never double-capture. (Mutex is per-session by default.)
        _instanceGate = new Mutex(initiallyOwned: true, "ManagerTool.Agent.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        var config = AgentConfig.Load();

        var host = new HostInfo(
            HostId: MachineId.Get(),
            MachineName: Environment.MachineName,
            UserName: Environment.UserName,
            AgentVersion: AgentConnection.AgentVersion,
            ConnectedAtUtc: DateTimeOffset.UtcNow);

        // Headless by design — no tray icon, no first-run popup, no local consent/install log.
        //
        // Employee disclosure and consent for THIS deployment are handled OUT OF BAND: the signed
        // employment agreement (written monitoring consent) plus the operator's hard-copy compliance
        // ledger are the authoritative system of record. An in-app notice or a local consent file is
        // therefore intentionally omitted — it would duplicate, and could contradict, the paper record.
        //
        // Do NOT re-add an on-screen notice or a local consent log without the operator's sign-off:
        // this configuration is deliberate, on company-owned equipment, backed by written consent.
        _connection = new AgentConnection(config, host);
        await _connection.StartAsync();
    }

    private async void OnExit(object sender, ExitEventArgs e)
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
        _instanceGate?.Dispose();
    }
}

/// <summary>Agent configuration, read from agent.config.json next to the exe.</summary>
public sealed record AgentConfig(
    string ServerUrl,
    string EnrollmentKey,
    bool AllowInvalidServerCert = false)
{
    public static AgentConfig Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "agent.config.json");
        if (File.Exists(path))
        {
            try
            {
                var cfg = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path));
                if (cfg is not null && !string.IsNullOrWhiteSpace(cfg.ServerUrl))
                    return cfg;
            }
            catch { /* fall through to default */ }
        }
        return new AgentConfig("https://localhost:5281", "CHANGE-ME-agent-enrollment-key", true);
    }
}
