using Microsoft.Data.Sqlite;
using ManagerTool.Shared;

namespace ManagerTool.Server;

/// <summary>
/// Append-only store of admin actions (the accountability trail). Shares the DB file with
/// the other stores. Writes are best-effort and never throw into the calling path — an
/// audit failure must not break the action being audited.
/// </summary>
public sealed class AuditStore
{
    private readonly string _connectionString;

    public AuditStore(string dbPath)
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
            CREATE TABLE IF NOT EXISTS admin_audit (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                ts_utc       TEXT NOT NULL,
                actor        TEXT NOT NULL,
                action       TEXT NOT NULL,
                target_host  TEXT,
                detail       TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_audit_ts ON admin_audit (ts_utc);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Record an action. Swallows failures so auditing never breaks the action.</summary>
    public void Log(string actor, string action, string? targetHost, string detail)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO admin_audit (ts_utc, actor, action, target_host, detail)
                VALUES ($ts, $actor, $action, $target, $detail);
                """;
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$actor", string.IsNullOrWhiteSpace(actor) ? "unknown" : actor);
            cmd.Parameters.AddWithValue("$action", action);
            cmd.Parameters.AddWithValue("$target", (object?)targetHost ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$detail", detail);
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // never throw into the audited action
        }
    }

    /// <param name="excludeHosts">
    /// Workstations the caller may not see. Entries targeting them are withheld, so the trail
    /// cannot be used to infer the existence of a blocked PC. Entries with no target are shown.
    /// </param>
    public IReadOnlyList<AuditEntry> Query(string? actor = null, string? action = null,
        int limit = 300, int offset = 0, IReadOnlyCollection<string>? excludeHosts = null)
    {
        limit = Math.Clamp(limit, 1, 5000);
        offset = Math.Max(0, offset);

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var sql = new System.Text.StringBuilder(
            "SELECT id, ts_utc, actor, action, target_host, detail FROM admin_audit WHERE 1=1");
        if (!string.IsNullOrWhiteSpace(actor)) { sql.Append(" AND actor=$actor"); cmd.Parameters.AddWithValue("$actor", actor); }
        if (!string.IsNullOrWhiteSpace(action)) { sql.Append(" AND action=$action"); cmd.Parameters.AddWithValue("$action", action); }
        if (excludeHosts is { Count: > 0 })
        {
            var names = new List<string>();
            var i = 0;
            foreach (var host in excludeHosts)
            {
                var name = "$x" + i++;
                names.Add(name);
                cmd.Parameters.AddWithValue(name, host);
            }
            sql.Append($" AND (target_host IS NULL OR target_host NOT IN ({string.Join(",", names)}))");
        }
        sql.Append(" ORDER BY id DESC LIMIT $limit OFFSET $offset;");
        cmd.CommandText = sql.ToString();
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);

        var list = new List<AuditEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new AuditEntry(
                r.GetInt64(0), DateTimeOffset.Parse(r.GetString(1)), r.GetString(2),
                r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5)));
        return list;
    }
}
