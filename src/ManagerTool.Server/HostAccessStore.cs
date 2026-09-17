using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace ManagerTool.Server;

/// <summary>
/// Per-viewer workstation deny-list. Access is deny-by-exception: an admin with no rows here
/// sees every PC, so newly enrolled machines are visible without the owner re-granting access.
///
/// The whole table is mirrored in memory because the live activity/alert fan-out consults it
/// once per connected admin per event; the DB is the durable copy, the cache is the hot path.
/// </summary>
public sealed class HostAccessStore
{
    private readonly string _connectionString;
    private readonly ConcurrentDictionary<string, HashSet<string>> _denied =
        new(StringComparer.OrdinalIgnoreCase);

    public HostAccessStore(string dbPath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        Initialize();
        Reload();
    }

    private SqliteConnection Open() { var c = new SqliteConnection(_connectionString); c.Open(); return c; }

    private void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS host_denies (
                username    TEXT NOT NULL COLLATE NOCASE,
                host_id     TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY (username, host_id)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private void Reload()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT username, host_id FROM host_denies;";
        var fresh = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (!fresh.TryGetValue(r.GetString(0), out var set))
                fresh[r.GetString(0)] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(r.GetString(1));
        }

        _denied.Clear();
        foreach (var (user, set) in fresh)
            _denied[user] = set;
    }

    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    /// <summary>Host ids this user may not see. Empty means unrestricted.</summary>
    public IReadOnlySet<string> DeniedFor(string username) =>
        _denied.TryGetValue(username, out var set) ? set : None;

    /// <summary>Replaces the user's blocked set wholesale. An empty list clears all blocks.</summary>
    public void SetDenied(string username, IReadOnlyCollection<string> hostIds)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM host_denies WHERE username = $u;";
            del.Parameters.AddWithValue("$u", username);
            del.ExecuteNonQuery();
        }

        var now = DateTimeOffset.UtcNow.ToString("o");
        foreach (var hostId in hostIds)
        {
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR REPLACE INTO host_denies (username, host_id, updated_utc) VALUES ($u, $h, $t);";
            ins.Parameters.AddWithValue("$u", username);
            ins.Parameters.AddWithValue("$h", hostId);
            ins.Parameters.AddWithValue("$t", now);
            ins.ExecuteNonQuery();
        }

        tx.Commit();

        if (hostIds.Count == 0)
            _denied.TryRemove(username, out _);
        else
            _denied[username] = new HashSet<string>(hostIds, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Drops every block for a user — called when their account is removed.</summary>
    public void Clear(string username) => SetDenied(username, Array.Empty<string>());
}
