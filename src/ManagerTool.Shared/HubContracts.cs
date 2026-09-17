namespace ManagerTool.Shared;

/// <summary>
/// Well-known SignalR route + group names.
/// </summary>
public static class HubRoutes
{
    public const string Path = "/hub";
    public const string AdminGroup = "admins";
    public static string HostGroup(string hostId) => $"host:{hostId}";

    /// <summary>
    /// Admins currently watching one host. Screen frames go only here — never to every admin —
    /// so a viewer never receives pixels from a workstation they are not watching, and cannot
    /// receive pixels at all from one they are blocked from.
    /// </summary>
    public static string ViewerGroup(string hostId) => $"view:{hostId}";
}

/// <summary>
/// Methods the SERVER exposes to callers (invoked by agents and admins).
/// Implemented on the server hub.
/// </summary>
public interface IMonitorHub
{
    // Agent -> Server
    Task RegisterHost(HostInfo info);
    Task PushActivity(ActivityEvent evt);
    Task PushFrame(ScreenFrame frame);        // legacy whole-screen frame (agents < 1.1)
    Task PushTiles(ScreenTileFrame frame);    // changed-tiles-only update (agents >= 1.1)
    Task ReportUpdateStatus(UpdateStatus status);  // agent's early "update triggered" signal (agents >= 1.2)

    // Admin -> Server (relayed to the target agent)
    Task StartViewing(string hostId);
    Task StopViewing(string hostId);
    Task SelectMonitor(string hostId, int monitorIndex);  // stream only this monitor

    // Admin -> Server: persist the live frames of a viewed host to disk.
    Task StartRecording(string hostId);
    Task StopRecording(string hostId);

    // Admin -> Server: instruct a host's agent to fully uninstall itself.
    Task RemoteUninstall(string hostId);

    // Admin -> Server: instruct a host's agent to update to the latest version.
    Task RemoteUpdate(string hostId);
}

/// <summary>
/// Methods the AGENT client implements (server -> agent pushes).
/// </summary>
public interface IAgentClient
{
    Task BeginScreenStream();  // an admin started viewing this host
    Task EndScreenStream();    // no admins viewing anymore
    Task SetMonitor(int monitorIndex);  // capture only this monitor (latency/bandwidth)
    Task RequestKeyframe();    // send a full frame next tick (a viewer joined / needs a fresh base)
    Task Uninstall();          // admin requested full removal of this agent
    Task Update();             // admin requested update to the latest version
}

/// <summary>
/// Methods the ADMIN client implements (server -> admin pushes).
/// </summary>
public interface IAdminClient
{
    Task HostConnected(HostInfo info);
    Task HostDisconnected(string hostId);
    Task ActivityReceived(ActivityEvent evt);
    Task FrameReceived(ScreenFrame frame);        // legacy whole-screen frame
    Task TilesReceived(ScreenTileFrame frame);    // changed-tiles update (keyframe or delta)
    Task AlertReceived(Alert alert);

    /// <summary>A host moved through an update stage (triggered/downloading/…/ok/rolledback).</summary>
    Task UpdateStatusChanged(UpdateStatus status);

    /// <summary>Who is watching a host right now, so every viewer can see they are not alone.</summary>
    Task ViewersChanged(string hostId, string[] viewers);

    /// <summary>
    /// The owner changed this admin's workstation access. Carries the full set now blocked, so an
    /// open view of any of them ends at once rather than freezing on its last frame. Receivers
    /// should also re-read their host list: PCs may equally have been unblocked.
    /// </summary>
    Task AccessChanged(string[] deniedHostIds);
}
