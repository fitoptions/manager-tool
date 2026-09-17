using Microsoft.Data.Sqlite;
using ManagerTool.Shared;

namespace ManagerTool.Server;

/// <summary>
/// SQLite-backed store for compliance rules and the alerts they fire. Shares the same
/// database file as the activity store. Seeds a few sensible default rules on first init
/// so alerting is useful out of the box.
/// </summary>
public sealed class AlertStore
{
    private readonly string _connectionString;

    public AlertStore(string dbPath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
        Initialize();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS rules (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                name         TEXT    NOT NULL,
                enabled      INTEGER NOT NULL DEFAULT 1,
                kind         TEXT    NOT NULL,
                pattern      TEXT    NOT NULL,
                severity     TEXT    NOT NULL,
                created_utc  TEXT    NOT NULL
            );
            CREATE TABLE IF NOT EXISTS alerts (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                ts_utc        TEXT    NOT NULL,
                host_id       TEXT    NOT NULL,
                machine_name  TEXT    NOT NULL,
                rule_id       INTEGER NOT NULL,
                rule_name     TEXT    NOT NULL,
                severity      TEXT    NOT NULL,
                process_name  TEXT    NOT NULL,
                window_title  TEXT    NOT NULL,
                url           TEXT,
                message       TEXT    NOT NULL,
                acknowledged  INTEGER NOT NULL DEFAULT 0,
                dedup_key     TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_alerts_ts ON alerts (ts_utc);
            CREATE INDEX IF NOT EXISTS ix_alerts_dedup ON alerts (dedup_key, ts_utc);
            """;
        cmd.ExecuteNonQuery();

        SeedDefaultsIfEmpty(conn);
    }

    private static void SeedDefaultsIfEmpty(SqliteConnection conn)
    {
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM rules;";
            if (Convert.ToInt64(check.ExecuteScalar()) > 0)
                return;
        }

        // Defaults tuned for a trading desk: unauthorized remote-access, VPNs, shadow-AI,
        // and off-hours activity. All are watchlists on focus metadata — no content capture.
        (string name, string kind, string pattern, string sev)[] defaults =
        {
            ("Remote-access tool", RuleKinds.App, "anydesk", Severities.Critical),
            ("Remote-access tool (TeamViewer)", RuleKinds.App, "teamviewer", Severities.Critical),
            ("Remote-access tool (UltraViewer)", RuleKinds.App, "ultraviewer", Severities.Critical),
            ("VPN client (WireGuard)", RuleKinds.App, "wireguard", Severities.Warning),
            ("Shadow-AI (ChatGPT)", RuleKinds.Url, "chatgpt.com", Severities.Warning),
            ("Shadow-AI (Gemini)", RuleKinds.Url, "gemini.google.com", Severities.Warning),
            ("Personal webmail", RuleKinds.Url, "mail.google.com", Severities.Info),
            ("After trading hours", RuleKinds.AfterHours, "08:45-15:45", Severities.Info),
        };

        var now = DateTimeOffset.UtcNow.ToString("o");
        foreach (var (name, kind, pattern, sev) in defaults)
        {
            using var ins = conn.CreateCommand();
            ins.CommandText = """
                INSERT INTO rules (name, enabled, kind, pattern, severity, created_utc)
                VALUES ($n, 1, $k, $p, $s, $c);
                """;
            ins.Parameters.AddWithValue("$n", name);
            ins.Parameters.AddWithValue("$k", kind);
            ins.Parameters.AddWithValue("$p", pattern);
            ins.Parameters.AddWithValue("$s", sev);
            ins.Parameters.AddWithValue("$c", now);
            ins.ExecuteNonQuery();
        }
    }

    // ---- Rules ----

    public IReadOnlyList<Rule> GetRules()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, enabled, kind, pattern, severity, created_utc FROM rules ORDER BY id;";
        var list = new List<Rule>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadRule(r));
        return list;
    }

    public Rule Create(RuleInput input)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rules (name, enabled, kind, pattern, severity, created_utc)
            VALUES ($n, $e, $k, $p, $s, $c);
            SELECT last_insert_rowid();
            """;
        var now = DateTimeOffset.UtcNow;
        BindRule(cmd, input, now);
        var id = Convert.ToInt64(cmd.ExecuteScalar());
        return new Rule(id, input.Name, input.Enabled, input.Kind, input.Pattern, input.Severity, now);
    }

    public bool Update(long id, RuleInput input)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE rules SET name=$n, enabled=$e, kind=$k, pattern=$p, severity=$s WHERE id=$id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$n", input.Name);
        cmd.Parameters.AddWithValue("$e", input.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$k", input.Kind);
        cmd.Parameters.AddWithValue("$p", input.Pattern);
        cmd.Parameters.AddWithValue("$s", input.Severity);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool Delete(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM rules WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    // ---- Alerts ----

    /// <summary>
    /// Persists an alert unless an identical one (same dedup key) fired within
    /// <paramref name="dedupMinutes"/>. Returns the stored alert, or null if deduped.
    /// </summary>
    public Alert? InsertIfNew(Alert alert, string dedupKey, int dedupMinutes = 5)
    {
        using var conn = Open();

        using (var dup = conn.CreateCommand())
        {
            dup.CommandText = "SELECT COUNT(*) FROM alerts WHERE dedup_key=$k AND ts_utc >= $since;";
            dup.Parameters.AddWithValue("$k", dedupKey);
            dup.Parameters.AddWithValue("$since", alert.TimestampUtc.AddMinutes(-dedupMinutes).ToString("o"));
            if (Convert.ToInt64(dup.ExecuteScalar()) > 0)
                return null;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO alerts (ts_utc, host_id, machine_name, rule_id, rule_name, severity,
                                process_name, window_title, url, message, acknowledged, dedup_key)
            VALUES ($ts, $host, $mn, $rid, $rn, $sev, $pn, $wt, $url, $msg, 0, $dk);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$ts", alert.TimestampUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$host", alert.HostId);
        cmd.Parameters.AddWithValue("$mn", alert.MachineName);
        cmd.Parameters.AddWithValue("$rid", alert.RuleId);
        cmd.Parameters.AddWithValue("$rn", alert.RuleName);
        cmd.Parameters.AddWithValue("$sev", alert.Severity);
        cmd.Parameters.AddWithValue("$pn", alert.ProcessName);
        cmd.Parameters.AddWithValue("$wt", alert.WindowTitle);
        cmd.Parameters.AddWithValue("$url", (object?)alert.Url ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$msg", alert.Message);
        cmd.Parameters.AddWithValue("$dk", dedupKey);
        var id = Convert.ToInt64(cmd.ExecuteScalar());
        return alert with { Id = id };
    }

    /// <param name="excludeHosts">Workstations the caller may not see; their alerts are omitted entirely.</param>
    public IReadOnlyList<Alert> QueryAlerts(
        string? hostId = null, string? severity = null, bool? acknowledged = null,
        int limit = 200, int offset = 0, IReadOnlyCollection<string>? excludeHosts = null)
    {
        limit = Math.Clamp(limit, 1, 2000);
        offset = Math.Max(0, offset);

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var sql = new System.Text.StringBuilder(
            "SELECT id, ts_utc, host_id, machine_name, rule_id, rule_name, severity, process_name, window_title, url, message, acknowledged FROM alerts WHERE 1=1");
        if (!string.IsNullOrWhiteSpace(hostId)) { sql.Append(" AND host_id=$host"); cmd.Parameters.AddWithValue("$host", hostId); }
        sql.Append(ExcludeClause(cmd, excludeHosts));
        if (!string.IsNullOrWhiteSpace(severity)) { sql.Append(" AND severity=$sev"); cmd.Parameters.AddWithValue("$sev", severity); }
        if (acknowledged is not null) { sql.Append(" AND acknowledged=$ack"); cmd.Parameters.AddWithValue("$ack", acknowledged.Value ? 1 : 0); }
        sql.Append(" ORDER BY id DESC LIMIT $limit OFFSET $offset;");
        cmd.CommandText = sql.ToString();
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);

        var list = new List<Alert>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadAlert(r));
        return list;
    }

    /// <summary>
    /// Acknowledges one alert. Returns false when the alert belongs to a workstation in
    /// <paramref name="excludeHosts"/>, so a blocked host's alerts cannot be acted on by id.
    /// </summary>
    public bool Acknowledge(long id, IReadOnlyCollection<string>? excludeHosts = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE alerts SET acknowledged=1 WHERE id=$id" + ExcludeClause(cmd, excludeHosts) + ";";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Marks every currently-unacknowledged visible alert as acknowledged. Returns how many were updated.</summary>
    public int AcknowledgeAll(IReadOnlyCollection<string>? excludeHosts = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE alerts SET acknowledged=1 WHERE acknowledged=0" + ExcludeClause(cmd, excludeHosts) + ";";
        return cmd.ExecuteNonQuery();
    }

    public int UnacknowledgedCount()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM alerts WHERE acknowledged=0;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>Builds " AND host_id NOT IN (…)" with bound parameters, or "" when unrestricted.</summary>
    private static string ExcludeClause(SqliteCommand cmd, IReadOnlyCollection<string>? excludeHosts)
    {
        if (excludeHosts is null || excludeHosts.Count == 0)
            return "";
        var names = new List<string>();
        var i = 0;
        foreach (var host in excludeHosts)
        {
            var name = "$x" + i++;
            names.Add(name);
            cmd.Parameters.AddWithValue(name, host);
        }
        return $" AND host_id NOT IN ({string.Join(",", names)})";
    }

    // ---- mapping helpers ----

    private static Rule ReadRule(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetInt64(2) != 0, r.GetString(3),
        r.GetString(4), r.GetString(5), DateTimeOffset.Parse(r.GetString(6)));

    private static Alert ReadAlert(SqliteDataReader r) => new(
        r.GetInt64(0), DateTimeOffset.Parse(r.GetString(1)), r.GetString(2), r.GetString(3),
        r.GetInt64(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8),
        r.IsDBNull(9) ? null : r.GetString(9), r.GetString(10), r.GetInt64(11) != 0);

    private static void BindRule(SqliteCommand cmd, RuleInput input, DateTimeOffset now)
    {
        cmd.Parameters.AddWithValue("$n", input.Name);
        cmd.Parameters.AddWithValue("$e", input.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$k", input.Kind);
        cmd.Parameters.AddWithValue("$p", input.Pattern);
        cmd.Parameters.AddWithValue("$s", input.Severity);
        cmd.Parameters.AddWithValue("$c", now.ToString("o"));
    }
}
