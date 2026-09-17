using Microsoft.Data.Sqlite;
using ManagerTool.Shared;

namespace ManagerTool.Server;

/// <summary>
/// Queue of "I forgot my password" requests awaiting owner action.
///
/// Deliberately holds no credential material: a row is only a flag saying an account's holder
/// says they are locked out. The owner still performs the reset, so a bogus row can at worst
/// waste their attention. One row per account (upsert), so repeated requests cannot grow the
/// table — a later request only refreshes the timestamp.
/// </summary>
public sealed class PasswordResetStore
{
    private readonly string _connectionString;

    public PasswordResetStore(string dbPath)
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
            CREATE TABLE IF NOT EXISTS password_reset_requests (
                username     TEXT PRIMARY KEY COLLATE NOCASE,
                requested_utc TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Raise (or refresh) the request for one account.</summary>
    public void Record(string username)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO password_reset_requests (username, requested_utc) VALUES ($u, $t)
            ON CONFLICT(username) DO UPDATE SET requested_utc = $t;
            """;
        cmd.Parameters.AddWithValue("$u", username);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Remove a request — because it was actioned, or dismissed as bogus.</summary>
    public bool Clear(string username)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM password_reset_requests WHERE username = $u;";
        cmd.Parameters.AddWithValue("$u", username);
        return cmd.ExecuteNonQuery() > 0;
    }

    public IReadOnlyList<PasswordResetTicket> List()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT username, requested_utc FROM password_reset_requests ORDER BY requested_utc;";
        var list = new List<PasswordResetTicket>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new PasswordResetTicket(r.GetString(0), DateTimeOffset.Parse(r.GetString(1))));
        return list;
    }
}
