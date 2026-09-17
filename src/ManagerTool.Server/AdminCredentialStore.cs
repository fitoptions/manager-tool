using Microsoft.Data.Sqlite;

namespace ManagerTool.Server;

/// <summary>Status of a non-owner admin account.</summary>
public static class AdminStatus
{
    public const string Pending = "pending";   // requested, awaiting owner approval — cannot log in
    public const string Active = "active";      // approved — can log in
}

/// <summary>A managed admin account (created via request/approval, stored in the DB).</summary>
public sealed record AdminUser(string Username, string Status, DateTimeOffset UpdatedUtc);

/// <summary>
/// Durable store of managed admin accounts and password overrides. Config-seeded admins are
/// the "owners"; everyone else is created here when the owner issues them an account.
/// The token service checks this store first (for overrides + additional admins) and falls
/// back to configuration, so password changes and new admins persist without editing config.
/// </summary>
public sealed class AdminCredentialStore
{
    private readonly string _connectionString;

    public AdminCredentialStore(string dbPath)
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
            CREATE TABLE IF NOT EXISTS admin_credentials (
                username      TEXT PRIMARY KEY COLLATE NOCASE,
                password_hash TEXT NOT NULL,
                status        TEXT NOT NULL DEFAULT 'active',
                updated_utc   TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Password hash for a username regardless of status, or null if unknown here.</summary>
    public string? GetHash(string username)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT password_hash FROM admin_credentials WHERE username = $u;";
        cmd.Parameters.AddWithValue("$u", username);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Password hash for a username only if the account is active.</summary>
    public string? GetActiveHash(string username)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT password_hash FROM admin_credentials WHERE username = $u AND status = 'active';";
        cmd.Parameters.AddWithValue("$u", username);
        return cmd.ExecuteScalar() as string;
    }

    public AdminUser? Get(string username)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT username, status, updated_utc FROM admin_credentials WHERE username = $u;";
        cmd.Parameters.AddWithValue("$u", username);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new AdminUser(r.GetString(0), r.GetString(1), DateTimeOffset.Parse(r.GetString(2))) : null;
    }

    public bool Exists(string username) => Get(username) is not null;

    /// <summary>Insert or update a username's hash + status.</summary>
    public void Upsert(string username, string passwordHash, string status)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO admin_credentials (username, password_hash, status, updated_utc)
            VALUES ($u, $h, $s, $t)
            ON CONFLICT(username) DO UPDATE SET password_hash = $h, status = $s, updated_utc = $t;
            """;
        cmd.Parameters.AddWithValue("$u", username);
        cmd.Parameters.AddWithValue("$h", passwordHash);
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Change only the password hash (keeps status). Returns false if the user is absent.</summary>
    public bool SetHash(string username, string passwordHash)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE admin_credentials SET password_hash = $h, updated_utc = $t WHERE username = $u;";
        cmd.Parameters.AddWithValue("$u", username);
        cmd.Parameters.AddWithValue("$h", passwordHash);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("o"));
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool SetStatus(string username, string status)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE admin_credentials SET status = $s, updated_utc = $t WHERE username = $u;";
        cmd.Parameters.AddWithValue("$u", username);
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("o"));
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool Delete(string username)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM admin_credentials WHERE username = $u;";
        cmd.Parameters.AddWithValue("$u", username);
        return cmd.ExecuteNonQuery() > 0;
    }

    public IReadOnlyList<AdminUser> List()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT username, status, updated_utc FROM admin_credentials ORDER BY status, username;";
        var list = new List<AdminUser>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new AdminUser(r.GetString(0), r.GetString(1), DateTimeOffset.Parse(r.GetString(2))));
        return list;
    }
}
