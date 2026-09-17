using Microsoft.Data.Sqlite;
using ManagerTool.Shared;

namespace ManagerTool.Server;

/// <summary>
/// Thin SQLite-backed store for the activity stream. Screen frames are intentionally
/// NOT persisted here by default — they are relayed live. Recording to disk is an
/// explicit admin action (out of scope for this scaffold; see TODO in the hub).
/// </summary>
public sealed class ActivityStore
{
    private readonly string _connectionString;

    public ActivityStore(string dbPath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        Initialize();
    }

    private void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS activity (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                host_id       TEXT    NOT NULL,
                ts_utc        TEXT    NOT NULL,
                process_name  TEXT    NOT NULL,
                window_title  TEXT    NOT NULL,
                url           TEXT,
                activity_kind TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_activity_host_ts ON activity (host_id, ts_utc);
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public void Insert(ActivityEvent e)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO activity (host_id, ts_utc, process_name, window_title, url, activity_kind)
            VALUES ($host, $ts, $proc, $title, $url, $kind);
            """;
        cmd.Parameters.AddWithValue("$host", e.HostId);
        cmd.Parameters.AddWithValue("$ts", e.TimestampUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$proc", e.ProcessName);
        cmd.Parameters.AddWithValue("$title", e.WindowTitle);
        cmd.Parameters.AddWithValue("$url", (object?)e.Url ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$kind", e.ActivityKind);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Deletes activity rows older than the cutoff. Returns the number of rows removed.
    /// Used by the retention policy to enforce the configured storage window.
    /// </summary>
    public int PurgeOlderThan(DateTimeOffset cutoffUtc)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM activity WHERE ts_utc < $cutoff;";
        cmd.Parameters.AddWithValue("$cutoff", cutoffUtc.ToString("o"));
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Filtered activity query, newest first. All filters are optional: a blank
    /// <paramref name="hostId"/> spans every workstation, <paramref name="fromUtc"/>/<paramref name="toUtc"/>
    /// bound the time window, <paramref name="search"/> matches process name / window title / URL
    /// (case-insensitive substring), <paramref name="excludeHosts"/> drops workstations the caller
    /// may not see, and <paramref name="offset"/>/<paramref name="limit"/> paginate.
    /// Fully parameterized — no user input is concatenated into SQL.
    /// </summary>
    public IReadOnlyList<ActivityEvent> Query(
        string? hostId,
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null,
        string? search = null,
        int limit = 200,
        int offset = 0,
        IReadOnlyCollection<string>? excludeHosts = null)
    {
        limit = Math.Clamp(limit, 1, 5000);
        offset = Math.Max(0, offset);

        using var conn = Open();
        using var cmd = conn.CreateCommand();

        var sql = new System.Text.StringBuilder(
            "SELECT host_id, ts_utc, process_name, window_title, url, activity_kind FROM activity WHERE 1=1");

        if (!string.IsNullOrWhiteSpace(hostId))
        {
            sql.Append(" AND host_id = $host");
            cmd.Parameters.AddWithValue("$host", hostId);
        }
        var excluded = excludeHosts?.ToList() ?? new List<string>();
        if (excluded.Count > 0)
        {
            sql.Append($" AND host_id NOT IN ({string.Join(",", excluded.Select((_, i) => "$x" + i))})");
            for (var i = 0; i < excluded.Count; i++) cmd.Parameters.AddWithValue("$x" + i, excluded[i]);
        }

        if (fromUtc is not null)
        {
            sql.Append(" AND ts_utc >= $from");
            cmd.Parameters.AddWithValue("$from", fromUtc.Value.ToString("o"));
        }
        if (toUtc is not null)
        {
            sql.Append(" AND ts_utc <= $to");
            cmd.Parameters.AddWithValue("$to", toUtc.Value.ToString("o"));
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            // LIKE with an escaped pattern; SQLite LIKE is case-insensitive for ASCII.
            sql.Append(" AND (process_name LIKE $q ESCAPE '\\' OR window_title LIKE $q ESCAPE '\\' OR url LIKE $q ESCAPE '\\')");
            cmd.Parameters.AddWithValue("$q", "%" + Escape(search.Trim()) + "%");
        }
        sql.Append(" ORDER BY id DESC LIMIT $limit OFFSET $offset;");

        cmd.CommandText = sql.ToString();
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);

        var results = new List<ActivityEvent>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new ActivityEvent(
                reader.GetString(0),
                DateTimeOffset.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5)));
        }
        return results;
    }

    // ---- Aggregations for reporting ----

    /// <summary>A grouped count row (top-app, top-url, or per-day).</summary>
    public sealed record Bucket(string Key, long Count);

    /// <param name="excludeHosts">
    /// Workstations the caller may not see. Applied to every aggregation so an unscoped report
    /// never totals in a PC the viewer is blocked from.
    /// </param>
    private (string where, Action<SqliteCommand> bind) Filter(
        string? hostId, DateTimeOffset? from, DateTimeOffset? to,
        IReadOnlyCollection<string>? excludeHosts = null)
    {
        var excluded = excludeHosts?.ToList() ?? new List<string>();

        var clauses = new List<string>();
        if (!string.IsNullOrWhiteSpace(hostId)) clauses.Add("host_id = $host");
        if (from is not null) clauses.Add("ts_utc >= $from");
        if (to is not null) clauses.Add("ts_utc <= $to");
        if (excluded.Count > 0)
            clauses.Add($"host_id NOT IN ({string.Join(",", excluded.Select((_, i) => "$x" + i))})");

        var where = clauses.Count > 0 ? " WHERE " + string.Join(" AND ", clauses) : "";
        void Bind(SqliteCommand c)
        {
            if (!string.IsNullOrWhiteSpace(hostId)) c.Parameters.AddWithValue("$host", hostId);
            if (from is not null) c.Parameters.AddWithValue("$from", from.Value.ToString("o"));
            if (to is not null) c.Parameters.AddWithValue("$to", to.Value.ToString("o"));
            for (var i = 0; i < excluded.Count; i++) c.Parameters.AddWithValue("$x" + i, excluded[i]);
        }
        return (where, Bind);
    }

    private IReadOnlyList<Bucket> GroupCount(string expr, string where, Action<SqliteCommand> bind, int limit, string? having = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {expr} AS k, COUNT(*) AS n FROM activity{where}" +
                          $" GROUP BY k{(having is null ? "" : " HAVING " + having)} ORDER BY n DESC LIMIT $limit;";
        bind(cmd);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        var list = new List<Bucket>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new Bucket(r.IsDBNull(0) ? "" : r.GetString(0), r.GetInt64(1)));
        return list;
    }

    public IReadOnlyList<Bucket> TopProcesses(string? host, DateTimeOffset? from, DateTimeOffset? to,
        IReadOnlyCollection<string>? excludeHosts = null, int limit = 15)
    { var (w, b) = Filter(host, from, to, excludeHosts); return GroupCount("process_name", w, b, limit); }

    public IReadOnlyList<Bucket> TopUrls(string? host, DateTimeOffset? from, DateTimeOffset? to,
        IReadOnlyCollection<string>? excludeHosts = null, int limit = 15)
    { var (w, b) = Filter(host, from, to, excludeHosts); return GroupCount("url", w, b, limit, having: "url IS NOT NULL AND url <> ''"); }

    public IReadOnlyList<Bucket> DailyCounts(string? host, DateTimeOffset? from, DateTimeOffset? to,
        IReadOnlyCollection<string>? excludeHosts = null, int limit = 60)
    { var (w, b) = Filter(host, from, to, excludeHosts); return GroupCount("substr(ts_utc,1,10)", w, b, limit); }

    public long CountEvents(string? host, DateTimeOffset? from, DateTimeOffset? to,
        IReadOnlyCollection<string>? excludeHosts = null)
    {
        var (w, b) = Filter(host, from, to, excludeHosts);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM activity{w};";
        b(cmd);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    // Escapes LIKE wildcards so a user searching for "%" or "_" gets a literal match.
    private static string Escape(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>Most recent events for one host, newest first.</summary>
    public IReadOnlyList<ActivityEvent> Recent(string hostId, int limit = 500)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT host_id, ts_utc, process_name, window_title, url, activity_kind
            FROM activity
            WHERE host_id = $host
            ORDER BY id DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$host", hostId);
        cmd.Parameters.AddWithValue("$limit", limit);

        var results = new List<ActivityEvent>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new ActivityEvent(
                reader.GetString(0),
                DateTimeOffset.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5)));
        }
        return results;
    }
}
