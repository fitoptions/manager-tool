using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using ManagerTool.Shared;

namespace ManagerTool.Server;

/// <summary>
/// Central relay. Agents register and push activity/frames; admins subscribe and
/// request live screen views. Screen streaming is reference-counted per host so an
/// agent only captures its display while at least one admin is actually watching —
/// several admins may watch the same host at once for the cost of one capture.
///
/// Every connection is authenticated; each method is gated to the caller's role, and
/// anything naming a host is additionally gated by <see cref="HostAccess"/>, so a viewer
/// blocked from a workstation can neither watch it nor learn that it exists.
/// </summary>
[Authorize]
public sealed class MonitorHub : Hub
{
    private readonly ActivityStore _store;
    private readonly HostRegistry _registry;
    private readonly Recorder _recorder;
    private readonly RuleEngine _rules;
    private readonly NotificationService _notifier;
    private readonly AuditStore _audit;
    private readonly HostAccess _access;
    private readonly AdminRegistry _admins;
    private readonly HostLabelStore _hosts;
    private readonly FrameRelay _relay;
    private readonly UpdateCoordinator _updates;

    public MonitorHub(ActivityStore store, HostRegistry registry, Recorder recorder,
        RuleEngine rules, NotificationService notifier, AuditStore audit,
        HostAccess access, AdminRegistry admins, HostLabelStore hosts, FrameRelay relay,
        UpdateCoordinator updates)
    {
        _relay = relay;
        _store = store;
        _registry = registry;
        _recorder = recorder;
        _rules = rules;
        _notifier = notifier;
        _audit = audit;
        _access = access;
        _admins = admins;
        _hosts = hosts;
        _updates = updates;
    }

    /// <summary>The signed-in admin's username, from the JWT subject claim.</summary>
    private string Actor =>
        Context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
        ?? Context.User?.Identity?.Name ?? "admin";

    private string MachineName(string hostId) =>
        _registry.Connected.FirstOrDefault(h => h.HostId == hostId)?.MachineName ?? hostId;

    /// <summary>Connections of the admins permitted to know anything about this host.</summary>
    private IReadOnlyList<string> Audience(string hostId) => _admins.ConnectionsAllowedTo(hostId, _access);

    /// <summary>
    /// Blocks a viewer from acting on a workstation they have no access to. Phrased so the
    /// response does not confirm whether the host exists.
    /// </summary>
    private void EnsureAllowed(string hostId)
    {
        if (!_access.CanSee(Actor, hostId))
            throw new HubException("That workstation is not available to your account.");
    }

    // A recording holds its own viewer reference so the agent keeps streaming after the admin
    // who started it disconnects. Keyed distinctly from any connection id.
    private static string RecorderRef(string hostId) => "recording:" + hostId;

    // ---- Agent -> Server ----

    [Authorize(Roles = Roles.Agent)]
    public async Task RegisterHost(HostInfo info)
    {
        _registry.Add(Context.ConnectionId, info);
        _hosts.Seen(info.HostId, info.MachineName);
        await Groups.AddToGroupAsync(Context.ConnectionId, HubRoutes.HostGroup(info.HostId));
        await Clients.Clients(Audience(info.HostId)).SendAsync(nameof(IAdminClient.HostConnected), info);

        // If admins were already watching when the agent dropped, resume streaming on reconnect —
        // the reference count never returned to zero, so a plain BeginView would not re-trigger it.
        if (_registry.ViewerCount(info.HostId) > 0)
            await Clients.Caller.SendAsync(nameof(IAgentClient.BeginScreenStream));
    }

    // The agent's early "I'm about to update" signal. The SYSTEM updater reports the later stages
    // over REST because it outlives the agent process, but this first ping means a triggered update
    // that then goes silent is visible as "triggered, never downloaded" — exactly today's failure.
    [Authorize(Roles = Roles.Agent)]
    public Task ReportUpdateStatus(UpdateStatus status)
    {
        // Attribute to the connection's own identity, never the payload's claimed host.
        var hostId = Context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                     ?? status.HostId;
        return _updates.ApplyAsync(status with { HostId = hostId, Utc = DateTimeOffset.UtcNow });
    }

    [Authorize(Roles = Roles.Agent)]
    public async Task PushActivity(ActivityEvent evt)
    {
        _store.Insert(evt);
        await Clients.Clients(Audience(evt.HostId)).SendAsync(nameof(IAdminClient.ActivityReceived), evt);

        // Evaluate compliance rules and push any newly-fired alerts live to admins,
        // plus fan out to external channels (email/Slack/Teams/webhook) per config.
        var machineName = _registry.Get(Context.ConnectionId)?.MachineName ?? evt.HostId;
        foreach (var alert in _rules.Evaluate(evt, machineName))
        {
            await Clients.Clients(Audience(evt.HostId)).SendAsync(nameof(IAdminClient.AlertReceived), alert);
            _notifier.Notify(alert);
        }
    }

    [Authorize(Roles = Roles.Agent)]
    public Task PushFrame(ScreenFrame frame)
    {
        // Legacy whole-screen path (agents < 1.1). Recordings keep the original full-resolution
        // frame; the live relay conflates and downscales before fanning out to the viewer group.
        _recorder.MaybeWrite(frame);
        _relay.Enqueue(frame);
        return Task.CompletedTask;
    }

    [Authorize(Roles = Roles.Agent)]
    public Task PushTiles(ScreenTileFrame frame)
    {
        // Tile-delta path (agents >= 1.1). The relay caches the latest tile of each cell so a
        // viewer who joins mid-stream can be handed a complete keyframe, merges pending deltas so a
        // slow link never backs up, and — when the host is being recorded — composites a full frame
        // to disk. Membership of the viewer group is granted in StartViewing, after the access check.
        _relay.EnqueueTiles(frame, _recorder);
        return Task.CompletedTask;
    }

    // ---- Admin -> Server (relayed to the target agent) ----

    [Authorize(Roles = Roles.Admin)]
    public async Task StartViewing(string hostId)
    {
        EnsureAllowed(hostId);
        await Groups.AddToGroupAsync(Context.ConnectionId, HubRoutes.ViewerGroup(hostId));

        var wasIdle = _registry.BeginView(hostId, Context.ConnectionId);
        if (wasIdle)
        {
            // Cold start: the agent's first frame will be a keyframe, so this joiner gets a complete
            // image with no extra round trip.
            await Clients.Group(HubRoutes.HostGroup(hostId)).SendAsync(nameof(IAgentClient.BeginScreenStream));
        }
        else if (_relay.TryBuildKeyframe(hostId, out var keyframe))
        {
            // Already streaming for someone else: hand the newcomer the cached full frame right away
            // rather than leaving them blank until the next tile happens to change.
            await Clients.Caller.SendAsync(nameof(IAdminClient.TilesReceived), keyframe);
        }
        else
        {
            // Streaming but nothing cached yet (a tile-capable agent that has not sent its first
            // frame). Nudge the agent to emit a keyframe so this joiner is not left blank.
            await Clients.Group(HubRoutes.HostGroup(hostId)).SendAsync(nameof(IAgentClient.RequestKeyframe));
        }

        // Only audit the first watch from this connection; the wall opens the same host once per
        // tile and pop-out, and an audit entry per tile would drown the trail.
        if (_registry.ViewCount(hostId, Context.ConnectionId) == 1)
            _audit.Log(Actor, AuditActions.ViewStart, hostId, $"Started viewing {MachineName(hostId)}");

        await AnnounceViewers(hostId);
    }

    [Authorize(Roles = Roles.Admin)]
    public async Task StopViewing(string hostId)
    {
        var nowIdle = _registry.EndView(hostId, Context.ConnectionId);
        if (_registry.ViewCount(hostId, Context.ConnectionId) == 0)
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, HubRoutes.ViewerGroup(hostId));
        if (nowIdle)
            await Clients.Group(HubRoutes.HostGroup(hostId)).SendAsync(nameof(IAgentClient.EndScreenStream));

        await AnnounceViewers(hostId);
    }

    [Authorize(Roles = Roles.Admin)]
    public async Task StartRecording(string hostId)
    {
        EnsureAllowed(hostId);

        // Recording holds a viewer reference so the agent keeps streaming even if the
        // admin isn't actively looking. Idempotent: a second start is a no-op.
        if (!_recorder.Start(hostId, Actor))
            return;

        _audit.Log(Actor, AuditActions.RecordStart, hostId, $"Started recording {MachineName(hostId)}");
        var wasIdle = _registry.BeginView(hostId, RecorderRef(hostId));
        if (wasIdle)
            await Clients.Group(HubRoutes.HostGroup(hostId)).SendAsync(nameof(IAgentClient.BeginScreenStream));
    }

    [Authorize(Roles = Roles.Admin)]
    public async Task StopRecording(string hostId)
    {
        EnsureAllowed(hostId);
        if (!_recorder.Stop(hostId))
            return;

        var nowIdle = _registry.EndView(hostId, RecorderRef(hostId));
        if (nowIdle)
            await Clients.Group(HubRoutes.HostGroup(hostId)).SendAsync(nameof(IAgentClient.EndScreenStream));
    }

    [Authorize(Roles = Roles.Admin)]
    public Task SelectMonitor(string hostId, int monitorIndex)
    {
        EnsureAllowed(hostId);
        // A different monitor is a different image; drop the stale tile cache so a late joiner is
        // not handed the old screen as a keyframe. The agent forces a keyframe on SetMonitor too.
        _relay.InvalidateCache(hostId);
        return Clients.Group(HubRoutes.HostGroup(hostId)).SendAsync(nameof(IAgentClient.SetMonitor), monitorIndex);
    }

    [Authorize(Roles = Roles.Admin)]
    public Task RemoteUninstall(string hostId)
    {
        EnsureAllowed(hostId);
        _audit.Log(Actor, AuditActions.RemoteUninstall, hostId, $"Requested uninstall of {MachineName(hostId)}");
        return Clients.Group(HubRoutes.HostGroup(hostId)).SendAsync(nameof(IAgentClient.Uninstall));
    }

    [Authorize(Roles = Roles.Admin)]
    public Task RemoteUpdate(string hostId)
    {
        EnsureAllowed(hostId);
        _audit.Log(Actor, AuditActions.RemoteUpdate, hostId, $"Requested update of {MachineName(hostId)}");
        return Clients.Group(HubRoutes.HostGroup(hostId)).SendAsync(nameof(IAgentClient.Update));
    }

    // ---- Connection lifecycle ----

    public override Task OnConnectedAsync()
    {
        // Group membership is driven by the authenticated role claim, not client-supplied
        // hints. Admins join the admin broadcast group; agents are grouped per-host on register.
        if (Context.User?.IsInRole(Roles.Admin) == true)
        {
            _admins.Add(Context.ConnectionId, Actor);
            Groups.AddToGroupAsync(Context.ConnectionId, HubRoutes.AdminGroup);
        }
        return base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _admins.Remove(Context.ConnectionId);

        // A viewer whose browser closed or dropped must not leave the agent capturing forever.
        foreach (var (hostId, nowIdle) in _registry.ReleaseAll(Context.ConnectionId))
        {
            if (nowIdle)
                await Clients.Group(HubRoutes.HostGroup(hostId)).SendAsync(nameof(IAgentClient.EndScreenStream));
            await AnnounceViewers(hostId);
        }

        var removed = _registry.Remove(Context.ConnectionId);
        if (removed is not null)
            await Clients.Clients(Audience(removed.HostId)).SendAsync(nameof(IAdminClient.HostDisconnected), removed.HostId);

        await base.OnDisconnectedAsync(exception);
    }

    private Task AnnounceViewers(string hostId)
    {
        var names = _registry.ViewersOf(hostId)
            .Select(_admins.UsernameFor)
            .Where(n => n is not null)
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Clients.Clients(Audience(hostId)).SendAsync(nameof(IAdminClient.ViewersChanged), hostId, names);
    }
}

/// <summary>Connected admin consoles, so host-scoped pushes can be addressed to just the permitted ones.</summary>
public sealed class AdminRegistry
{
    private readonly ConcurrentDictionary<string, string> _byConnection = new();

    public void Add(string connectionId, string username) => _byConnection[connectionId] = username;

    public void Remove(string connectionId) => _byConnection.TryRemove(connectionId, out _);

    public string? UsernameFor(string connectionId) =>
        _byConnection.TryGetValue(connectionId, out var u) ? u : null;

    /// <summary>Every open session of one admin — a person may have the console up on several screens.</summary>
    public IReadOnlyList<string> ConnectionsFor(string username) =>
        _byConnection.Where(kv => string.Equals(kv.Value, username, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key).ToList();

    public IReadOnlyList<string> ConnectionsAllowedTo(string hostId, HostAccess access) =>
        _byConnection.Where(kv => access.CanSee(kv.Value, hostId)).Select(kv => kv.Key).ToList();
}

/// <summary>
/// In-memory view of connected hosts plus, per host, who is watching it. Views are counted per
/// viewer (an admin connection, or a recording session) so several admins can watch one host
/// while the agent captures exactly once, and a dropped connection releases only its own holds.
/// </summary>
public sealed class HostRegistry
{
    private readonly ConcurrentDictionary<string, HostInfo> _byConnection = new();

    // HostId -> the one connection currently allowed to speak for it. This is what makes a
    // reinstall/update/reconnect take over the dashboard row IMMEDIATELY instead of waiting for
    // SignalR to notice the old connection is dead (keep-alive/client-timeout, or longer if a
    // proxy in front swallows the close) — without it, a killed-but-not-yet-detected old agent
    // and a freshly-registered new one both sit in _byConnection, and Connected (below) would
    // show the same physical PC twice, or show the old, stale entry ahead of the new one.
    private readonly ConcurrentDictionary<string, string> _connectionByHost = new();

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _viewers = new();

    public void Add(string connectionId, HostInfo info)
    {
        _byConnection[connectionId] = info;

        var previousConnectionId = _connectionByHost.GetOrAdd(info.HostId, connectionId);
        if (previousConnectionId != connectionId)
        {
            _connectionByHost[info.HostId] = connectionId;
            _byConnection.TryRemove(previousConnectionId, out _);   // evict the superseded entry now
        }
    }

    public HostInfo? Get(string connectionId) =>
        _byConnection.TryGetValue(connectionId, out var info) ? info : null;

    public HostInfo? Remove(string connectionId)
    {
        if (!_byConnection.TryRemove(connectionId, out var info)) return null;

        // Only clear the host's pointer if it still names THIS connection — a disconnect
        // notification arriving late for an old connection must never evict a newer one that
        // has already taken over this HostId via Add above.
        ((ICollection<KeyValuePair<string, string>>)_connectionByHost)
            .Remove(new KeyValuePair<string, string>(info.HostId, connectionId));
        return info;
    }

    public IReadOnlyCollection<HostInfo> Connected => _byConnection.Values.ToList();

    private ConcurrentDictionary<string, int> Holds(string hostId) =>
        _viewers.GetOrAdd(hostId, _ => new ConcurrentDictionary<string, int>());

    /// <returns>true if this is the first viewer of the host (the agent should start streaming).</returns>
    public bool BeginView(string hostId, string viewerId)
    {
        var holds = Holds(hostId);
        var wasIdle = holds.IsEmpty;
        holds.AddOrUpdate(viewerId, 1, (_, c) => c + 1);
        return wasIdle;
    }

    /// <returns>true if that was the last viewer of the host (the agent should stop streaming).</returns>
    public bool EndView(string hostId, string viewerId)
    {
        if (!_viewers.TryGetValue(hostId, out var holds))
            return false;

        if (holds.TryGetValue(viewerId, out var count))
        {
            if (count <= 1) holds.TryRemove(viewerId, out _);
            else holds[viewerId] = count - 1;
        }
        return holds.IsEmpty;
    }

    /// <summary>
    /// Drops every hold a disconnected viewer had.
    /// </summary>
    /// <returns>Each host it was viewing, with whether that host now has no viewers at all.</returns>
    public IReadOnlyList<(string HostId, bool NowIdle)> ReleaseAll(string viewerId)
    {
        var released = new List<(string, bool)>();
        foreach (var (hostId, holds) in _viewers)
        {
            if (holds.TryRemove(viewerId, out _))
                released.Add((hostId, holds.IsEmpty));
        }
        return released;
    }

    /// <summary>
    /// Drops every hold one viewer has on one host at once — used when access is revoked mid-session,
    /// where the viewer will not be sending the matching StopViewing calls.
    /// </summary>
    /// <returns>true if the removal left the host with no viewers at all.</returns>
    public bool ReleaseHold(string hostId, string viewerId)
    {
        if (!_viewers.TryGetValue(hostId, out var holds) || !holds.TryRemove(viewerId, out _))
            return false;
        return holds.IsEmpty;
    }

    public int ViewerCount(string hostId) =>
        _viewers.TryGetValue(hostId, out var holds) ? holds.Count : 0;

    public int ViewCount(string hostId, string viewerId) =>
        _viewers.TryGetValue(hostId, out var holds) && holds.TryGetValue(viewerId, out var c) ? c : 0;

    public IReadOnlyList<string> ViewersOf(string hostId) =>
        _viewers.TryGetValue(hostId, out var holds) ? holds.Keys.ToList() : Array.Empty<string>();
}
