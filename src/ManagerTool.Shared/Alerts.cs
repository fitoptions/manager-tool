namespace ManagerTool.Shared;

/// <summary>Rule match kinds.</summary>
public static class RuleKinds
{
    public const string App = "app";                 // process name contains pattern
    public const string Url = "url";                 // browser URL contains pattern
    public const string Window = "window";           // window title contains pattern
    public const string AfterHours = "after_hours";  // activity outside "HH:mm-HH:mm" (IST)
}

/// <summary>Alert severity levels, least → most severe.</summary>
public static class Severities
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Critical = "critical";
}

/// <summary>
/// A compliance/acceptable-use rule evaluated against every activity event. For app/url/window
/// kinds, <see cref="Pattern"/> is a case-insensitive substring (a watchlist term). For
/// after_hours, <see cref="Pattern"/> is the allowed business window as "HH:mm-HH:mm" (IST) —
/// activity outside it fires the alert.
/// </summary>
public sealed record Rule(
    long Id,
    string Name,
    bool Enabled,
    string Kind,
    string Pattern,
    string Severity,
    DateTimeOffset CreatedUtc);

/// <summary>Create/update payload for a rule (no server-assigned fields).</summary>
public sealed record RuleInput(
    string Name,
    bool Enabled,
    string Kind,
    string Pattern,
    string Severity);

/// <summary>A fired alert — one activity event matched one enabled rule.</summary>
public sealed record Alert(
    long Id,
    DateTimeOffset TimestampUtc,
    string HostId,
    string MachineName,
    long RuleId,
    string RuleName,
    string Severity,
    string ProcessName,
    string WindowTitle,
    string? Url,
    string Message,
    bool Acknowledged);
