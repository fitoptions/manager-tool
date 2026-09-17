using ManagerTool.Shared;
using Microsoft.AspNetCore.SignalR;

namespace ManagerTool.Server;

/// <summary>
/// The one place a host's update-status report is applied: persist it, push it live to the admins
/// permitted to see that host, and — when the canary confirms it is healthy on the target — open the
/// rollout to the rest of the fleet. Shared by the hub (agent's "triggered" signal) and the REST
/// endpoint (the SYSTEM updater's stage reports), so both paths behave identically.
/// </summary>
public sealed class UpdateCoordinator
{
    private readonly UpdateStore _store;
    private readonly AdminRegistry _admins;
    private readonly HostAccess _access;
    private readonly IHubContext<MonitorHub> _hub;

    public UpdateCoordinator(UpdateStore store, AdminRegistry admins, HostAccess access, IHubContext<MonitorHub> hub)
    {
        _store = store;
        _admins = admins;
        _access = access;
        _hub = hub;
    }

    public async Task ApplyAsync(UpdateStatus status)
    {
        _store.SetStatus(status);

        var conns = _admins.ConnectionsAllowedTo(status.HostId, _access);
        if (conns.Count > 0)
            await _hub.Clients.Clients(conns).SendAsync(nameof(IAdminClient.UpdateStatusChanged), status);

        // Canary-staged advance: the moment the chosen canary reports a healthy install of the
        // target version, open the fleet so every other host's next poll gets the go-ahead.
        if (status.Stage == "ok")
        {
            var p = _store.GetRollout();
            if (p is { FleetOpen: false }
                && p.CanaryHostId == status.HostId
                && UpdateStore.CompareVersions(status.Version, p.TargetVersion) >= 0)
            {
                _store.OpenFleet();
            }
        }
    }
}
