namespace ManagerTool.Shared;

/// <summary>Well-known admin audit action names.</summary>
public static class AuditActions
{
    public const string Login = "login";
    public const string ViewStart = "view-start";
    public const string ViewStop = "view-stop";
    public const string RecordStart = "record-start";
    public const string RecordStop = "record-stop";
    public const string RemoteUninstall = "remote-uninstall";
    public const string RemoteUpdate = "remote-update";
    public const string RolloutSet = "rollout-set";
    public const string RuleCreate = "rule-create";
    public const string RuleUpdate = "rule-update";
    public const string RuleDelete = "rule-delete";
    public const string AlertAck = "alert-ack";
    public const string Export = "export";
    public const string CategoryChange = "category-change";
    public const string AccessChange = "access-change";
}

/// <summary>
/// One immutable record of an admin action — who did what, to which host, and when.
/// The accountability trail for the watchers themselves.
/// </summary>
public sealed record AuditEntry(
    long Id,
    DateTimeOffset TimestampUtc,
    string Actor,
    string Action,
    string? TargetHost,
    string Detail);
