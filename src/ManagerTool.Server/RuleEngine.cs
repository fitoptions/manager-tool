using System.Collections.Concurrent;
using ManagerTool.Shared;

namespace ManagerTool.Server;

/// <summary>
/// Evaluates each activity event against the enabled rule set and persists any resulting
/// alerts (deduped). Rules are cached in memory and reloaded on change and periodically,
/// so evaluation on the hot PushActivity path never hits the database for rule lookup.
/// </summary>
public sealed class RuleEngine
{
    // India Standard Time — the desk's local time, used for after-hours evaluation.
    private static readonly TimeSpan IstOffset = TimeSpan.FromMinutes(330);

    private readonly AlertStore _store;
    private volatile IReadOnlyList<Rule> _enabled = Array.Empty<Rule>();

    public RuleEngine(AlertStore store)
    {
        _store = store;
        Reload();
    }

    /// <summary>Refresh the cached enabled-rule set from the store.</summary>
    public void Reload() =>
        _enabled = _store.GetRules().Where(r => r.Enabled).ToList();

    /// <summary>
    /// Evaluate one event; persist and return every newly-fired (non-deduped) alert.
    /// </summary>
    public IReadOnlyList<Alert> Evaluate(ActivityEvent evt, string machineName)
    {
        var rules = _enabled;
        if (rules.Count == 0)
            return Array.Empty<Alert>();

        List<Alert>? fired = null;
        foreach (var rule in rules)
        {
            if (!TryMatch(rule, evt, out var message, out var dedupSuffix, out var dedupMinutes))
                continue;

            var alert = new Alert(
                Id: 0,
                TimestampUtc: evt.TimestampUtc,
                HostId: evt.HostId,
                MachineName: machineName,
                RuleId: rule.Id,
                RuleName: rule.Name,
                Severity: rule.Severity,
                ProcessName: evt.ProcessName,
                WindowTitle: evt.WindowTitle,
                Url: evt.Url,
                Message: message,
                Acknowledged: false);

            var dedupKey = $"{rule.Id}|{evt.HostId}|{dedupSuffix}";
            var stored = _store.InsertIfNew(alert, dedupKey, dedupMinutes);
            if (stored is not null)
                (fired ??= new List<Alert>()).Add(stored);
        }
        return (IReadOnlyList<Alert>?)fired ?? Array.Empty<Alert>();
    }

    private static bool TryMatch(Rule rule, ActivityEvent evt,
        out string message, out string dedupSuffix, out int dedupMinutes)
    {
        message = ""; dedupSuffix = ""; dedupMinutes = 5;

        switch (rule.Kind)
        {
            case RuleKinds.App:
                if (Contains(evt.ProcessName, rule.Pattern))
                {
                    message = $"{evt.ProcessName} matched app watchlist “{rule.Pattern}”";
                    dedupSuffix = evt.ProcessName.ToLowerInvariant();
                    return true;
                }
                return false;

            case RuleKinds.Url:
                if (evt.Url is not null && Contains(evt.Url, rule.Pattern))
                {
                    message = $"visited {evt.Url} (matched “{rule.Pattern}”)";
                    dedupSuffix = rule.Pattern.ToLowerInvariant();
                    return true;
                }
                return false;

            case RuleKinds.Window:
                if (Contains(evt.WindowTitle, rule.Pattern))
                {
                    message = $"window “{evt.WindowTitle}” matched “{rule.Pattern}”";
                    dedupSuffix = evt.WindowTitle.ToLowerInvariant();
                    return true;
                }
                return false;

            case RuleKinds.AfterHours:
                if (IsAfterHours(evt.TimestampUtc, rule.Pattern, out var istLocal))
                {
                    message = $"activity at {istLocal:HH:mm} IST is outside hours {rule.Pattern}";
                    dedupSuffix = $"afterhours|{istLocal:yyyyMMdd}";  // ~one per host per day
                    dedupMinutes = 180;
                    return true;
                }
                return false;

            default:
                return false;
        }
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>Pattern is "HH:mm-HH:mm" business hours in IST; returns true if outside it.</summary>
    private static bool IsAfterHours(DateTimeOffset tsUtc, string pattern, out DateTimeOffset istLocal)
    {
        istLocal = tsUtc.ToOffset(IstOffset);
        var parts = pattern.Split('-');
        if (parts.Length != 2 ||
            !TimeSpan.TryParse(parts[0].Trim(), out var start) ||
            !TimeSpan.TryParse(parts[1].Trim(), out var end))
            return false;   // malformed rule → never fires

        var t = istLocal.TimeOfDay;
        var withinHours = t >= start && t <= end;
        return !withinHours;
    }
}
