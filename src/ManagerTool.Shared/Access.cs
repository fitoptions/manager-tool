namespace ManagerTool.Shared;

/// <summary>
/// A workstation the server has ever seen enrol, online or not. The owner picks from this
/// list when blocking PCs for a viewer, so a PC can be blocked while it is switched off.
/// </summary>
public sealed record KnownHost(
    string HostId,
    string MachineName,
    DateTimeOffset LastSeenUtc);

/// <summary>
/// Owner request body for setting one viewer's blocked workstations. Access is a deny-list:
/// an empty array means the viewer sees every PC, including ones enrolled later.
/// </summary>
public sealed record HostAccessInput(string[] DeniedHostIds);

/// <summary>A workstation as the access editor lists it — offline ones stay pickable.</summary>
public sealed record AccessHost(
    string HostId,
    string MachineName,
    string? Nickname,
    DateTimeOffset LastSeenUtc,
    bool Online)
{
    /// <summary>Nickname when the owner has set one, else the raw machine name.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Nickname) ? MachineName : Nickname!;
}

/// <summary>One non-owner admin and the workstations currently blocked for them.</summary>
public sealed record AccessViewer(string Username, string Status, string[] DeniedHostIds);

/// <summary>Everything the owner needs to edit workstation access in one round trip.</summary>
public sealed record AccessMatrix(AccessHost[] Hosts, AccessViewer[] Viewers);
