using Microsoft.Data.Sqlite;
using ManagerTool.Shared;

namespace ManagerTool.Server;

/// <summary>Request body for setting a host nickname (blank = clear).</summary>
public sealed record HostLabelInput(string? Nickname);

/// <summary>
/// Directory of monitored PCs: admin-assigned nicknames (hostId → friendly name), so the console
/// can show "Rahul – Options Desk" instead of "DESKTOP-Q3BQ628", plus the roll of every host that
/// has ever enrolled. The roll is what the owner picks from when blocking a PC for a viewer, so a
/// switched-off machine can still be managed. Persisted; survives reconnects.
/// </summary>
public sealed class HostLabelStore
{
    private readonly string _connectionString;

    public HostLabelStore(string dbPath)
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
            CREATE TABLE IF NOT EXISTS host_labels (
                host_id     TEXT PRIMARY KEY,
                nickname    TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS known_hosts (
                host_id       TEXT PRIMARY KEY,
                machine_name  TEXT NOT NULL,
                last_seen_utc TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Records that a host enrolled, so it stays pickable after it goes offline.</summary>
    public void Seen(string hostId, string machineName)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO known_hosts (host_id, machine_name, last_seen_utc) VALUES ($h, $m, $t)
            ON CONFLICT(host_id) DO UPDATE SET machine_name = $m, last_seen_utc = $t;
            """;
        cmd.Parameters.AddWithValue("$h", hostId);
        cmd.Parameters.AddWithValue("$m", machineName);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Every host ever enrolled, most recently seen first.</summary>
    public IReadOnlyList<KnownHost> KnownHosts()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT host_id, machine_name, last_seen_utc FROM known_hosts ORDER BY last_seen_utc DESC;";
        var list = new List<KnownHost>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new KnownHost(r.GetString(0), r.GetString(1), DateTimeOffset.Parse(r.GetString(2))));
        return list;
    }

    /// <summary>All labels as a hostId → nickname map.</summary>
    public IReadOnlyDictionary<string, string> GetAll()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT host_id, nickname FROM host_labels;";
        var map = new Dictionary<string, string>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            map[r.GetString(0)] = r.GetString(1);
        return map;
    }

    /// <summary>Set a nickname, or clear it (revert to machine name) when blank.</summary>
    public void Set(string hostId, string? nickname)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        if (string.IsNullOrWhiteSpace(nickname))
        {
            cmd.CommandText = "DELETE FROM host_labels WHERE host_id = $h;";
            cmd.Parameters.AddWithValue("$h", hostId);
        }
        else
        {
            cmd.CommandText = """
                INSERT INTO host_labels (host_id, nickname, updated_utc) VALUES ($h, $n, $t)
                ON CONFLICT(host_id) DO UPDATE SET nickname = $n, updated_utc = $t;
                """;
            cmd.Parameters.AddWithValue("$h", hostId);
            cmd.Parameters.AddWithValue("$n", nickname.Trim());
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("o"));
        }
        cmd.ExecuteNonQuery();
    }
}
