using Microsoft.Data.Sqlite;
using ManagerTool.Shared;

namespace ManagerTool.Server;

/// <summary>
/// Persists the self-update system's state so the dashboard is never blind again:
///   - update_status: the latest lifecycle stage each host reported (survives disconnect + restart,
///     so a host that installed then went dark still shows WHY).
///   - rollout: the single canary-staged rollout policy (target version + chosen canary + whether
///     the fleet has been opened). Setting a new target holds the fleet until the canary is healthy.
/// </summary>
public sealed class UpdateStore
{
    private readonly string _connectionString;

    public UpdateStore(string dbPath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        Initialize();
    }

    private SqliteConnection Open() { var c = new SqliteConnection(_connectionString); c.Open(); return c; }

    private void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS update_status (
                host_id TEXT PRIMARY KEY,
                stage   TEXT NOT NULL,
                version TEXT,
                detail  TEXT,
                utc     TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS rollout (
                id             INTEGER PRIMARY KEY CHECK (id = 1),
                target_version TEXT NOT NULL,
                canary_host_id TEXT,
                fleet_open     INTEGER NOT NULL,
                updated_utc    TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    // ---- per-host update status ----

    public void SetStatus(UpdateStatus s)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO update_status (host_id, stage, version, detail, utc)
            VALUES ($h, $s, $v, $d, $t)
            ON CONFLICT(host_id) DO UPDATE SET stage = $s, version = $v, detail = $d, utc = $t;
            """;
        cmd.Parameters.AddWithValue("$h", s.HostId);
        cmd.Parameters.AddWithValue("$s", s.Stage);
        cmd.Parameters.AddWithValue("$v", (object?)s.Version ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$d", (object?)s.Detail ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", s.Utc.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public UpdateStatus? GetStatus(string hostId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT host_id, stage, version, detail, utc FROM update_status WHERE host_id = $h;";
        cmd.Parameters.AddWithValue("$h", hostId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new UpdateStatus(
            r.GetString(0), r.GetString(1),
            r.IsDBNull(2) ? null : r.GetString(2),
            r.IsDBNull(3) ? null : r.GetString(3),
            DateTimeOffset.Parse(r.GetString(4)));
    }

    public IReadOnlyList<UpdateStatus> AllStatus()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT host_id, stage, version, detail, utc FROM update_status;";
        var list = new List<UpdateStatus>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new UpdateStatus(
                r.GetString(0), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                DateTimeOffset.Parse(r.GetString(4))));
        return list;
    }

    // ---- rollout policy (single row) ----

    public RolloutPolicy? GetRollout()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT target_version, canary_host_id, fleet_open, updated_utc FROM rollout WHERE id = 1;";
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new RolloutPolicy(
            r.GetString(0),
            r.IsDBNull(1) ? null : r.GetString(1),
            r.GetInt32(2) != 0,
            DateTimeOffset.Parse(r.GetString(3)));
    }

    /// <summary>Set a new target + canary. Holds the fleet (fleet_open = canary is null) until healthy.</summary>
    public void SetRollout(string targetVersion, string? canaryHostId)
    {
        // With no canary, there is nothing to gate on, so the fleet is open immediately.
        var fleetOpen = string.IsNullOrWhiteSpace(canaryHostId);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rollout (id, target_version, canary_host_id, fleet_open, updated_utc)
            VALUES (1, $v, $c, $f, $t)
            ON CONFLICT(id) DO UPDATE SET target_version = $v, canary_host_id = $c, fleet_open = $f, updated_utc = $t;
            """;
        cmd.Parameters.AddWithValue("$v", targetVersion);
        cmd.Parameters.AddWithValue("$c", (object?)canaryHostId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$f", fleetOpen ? 1 : 0);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Open the fleet — called automatically once the canary reports healthy on target.</summary>
    public void OpenFleet()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE rollout SET fleet_open = 1, updated_utc = $t WHERE id = 1;";
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Compare dotted versions: &lt;0 if a&lt;b, 0 equal, &gt;0 if a&gt;b. Missing parts count as 0.</summary>
    public static int CompareVersions(string? a, string? b)
    {
        int[] pa = Parse(a), pb = Parse(b);
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            int x = i < pa.Length ? pa[i] : 0, y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x < y ? -1 : 1;
        }
        return 0;

        static int[] Parse(string? v) =>
            (v ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries)
                     .Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
    }
}
