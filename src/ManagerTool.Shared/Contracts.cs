namespace ManagerTool.Shared;

/// <summary>
/// Identifies a monitored workstation. Sent once on connect and cached server-side.
/// </summary>
public sealed record HostInfo(
    string HostId,        // stable per-machine id (see Agent: MachineId.cs)
    string MachineName,
    string UserName,
    string AgentVersion,
    DateTimeOffset ConnectedAtUtc);

/// <summary>
/// A single point in a host's self-update lifecycle, reported by the agent (hub) for the early
/// "triggered" signal and by the SYSTEM updater (REST) for every stage after — because the updater
/// survives the agent being killed mid-install, it is the reliable reporter. This is what turns the
/// update path from a silent black box into something the dashboard can show per host.
/// </summary>
public sealed record UpdateStatus(
    string HostId,
    string Stage,          // triggered | downloading | installing | verifying | ok | failed | rolledback
    string? Version,       // target version while in progress; the running version once ok
    string? Detail,        // human-readable note, especially the reason on failed/rolledback
    DateTimeOffset Utc);

/// <summary>
/// The server's answer to an agent asking "is it my turn to update?". Canary-staged: the chosen
/// canary host is told to update first; the rest are held until the canary reports healthy.
/// </summary>
public sealed record RolloutDecision(
    bool ShouldUpdate,
    string TargetVersion,
    string Reason);

/// <summary>
/// Admin-controlled rollout policy. Setting a new target with a canary holds the fleet
/// (FleetOpen=false) until the canary confirms healthy on that target, then opens automatically.
/// </summary>
public sealed record RolloutPolicy(
    string TargetVersion,
    string? CanaryHostId,
    bool FleetOpen,
    DateTimeOffset UpdatedUtc = default);

/// <summary>Admin request to start/adjust a rollout. FleetOpen is derived server-side, not set here.</summary>
public sealed record RolloutRequest(
    string TargetVersion,
    string? CanaryHostId);

/// <summary>
/// A single "what was in focus" record. This is the tagged, timestamped activity
/// stream the desk uses for compliance review. No keystroke content is ever captured.
/// </summary>
public sealed record ActivityEvent(
    string HostId,
    DateTimeOffset TimestampUtc,
    string ProcessName,       // e.g. "chrome", "TradingTerminal"
    string WindowTitle,       // foreground window title
    string? Url,              // best-effort browser URL when the foreground app is a browser
    string ActivityKind);     // "window-focus" | "url" | "process-start" | "process-stop"

/// <summary>
/// One captured display frame, sent only while an admin is actively viewing this host.
/// Screen capture is on-demand (pull), never continuous background recording, unless
/// an admin explicitly starts a recording session.
///
/// On a multi-monitor host the agent emits one frame per monitor each tick, tagged with
/// <see cref="MonitorIndex"/> (0-based) out of <see cref="MonitorCount"/>.
/// </summary>
public sealed record ScreenFrame(
    string HostId,
    DateTimeOffset TimestampUtc,
    int Width,
    int Height,
    byte[] JpegBytes,
    int MonitorIndex = 0,
    int MonitorCount = 1);

/// <summary>One JPEG-encoded cell of a tiled screen frame, addressed by its grid index.</summary>
public sealed record ScreenTile(int Index, byte[] JpegBytes);

/// <summary>
/// A tiled screen update — the low-latency alternative to <see cref="ScreenFrame"/>.
///
/// The streamed monitor is divided into a <see cref="Cols"/>×<see cref="Rows"/> grid of
/// <see cref="TileSize"/>-pixel cells. Each tick the agent sends only the cells whose pixels
/// changed, so a mostly-static screen (a trading terminal between ticks) sends almost nothing
/// and updates arrive far sooner than a whole-screen JPEG would allow. A <see cref="Keyframe"/>
/// carries every cell — sent as the first frame of a stream, to a viewer who just joined, or
/// when the monitor selection changes — so a receiver can always paint a complete image before
/// deltas refine it.
///
/// Tile <c>Index = row * Cols + col</c>; its pixel origin is <c>(col*TileSize, row*TileSize)</c>
/// and its size is clamped at the right/bottom edges to stay within
/// <see cref="FrameWidth"/>×<see cref="FrameHeight"/>.
/// </summary>
public sealed record ScreenTileFrame(
    string HostId,
    DateTimeOffset TimestampUtc,
    int MonitorIndex,
    int MonitorCount,
    int FrameWidth,
    int FrameHeight,
    int TileSize,
    int Cols,
    int Rows,
    bool Keyframe,
    ScreenTile[] Tiles);

/// <summary>
/// Central place for the disclosure text shown to every employee. Surveillance is
/// transparent by policy: the agent renders this and refuses to run headless-hidden.
/// </summary>
public static class MonitoringNotice
{
    public const string Title = "This workstation is monitored";

    public const string Body =
        "This is a company-owned trading workstation. Screen activity, the application " +
        "and window currently in focus, and websites visited are recorded for compliance " +
        "and security purposes while you are signed in.\n\n" +
        "Keystrokes are NOT recorded. Use a personal device for personal accounts.\n\n" +
        "By using this workstation you acknowledge this monitoring policy.";
}
