using Microsoft.Data.Sqlite;

namespace ManagerTool.Server;

/// <summary>Why an OTP verification failed. Callers must NOT surface the distinction — see remarks.</summary>
/// <remarks>
/// The reasons exist for the audit log only. Telling the caller apart "no code outstanding" from
/// "wrong code" would confirm which usernames have a live reset in flight, so every failure is
/// reported to the client with one generic message.
/// </remarks>
public enum OtpResult
{
    Ok,
    NoneOutstanding,
    Expired,
    TooManyAttempts,
    WrongCode,
}

/// <summary>
/// One-time codes for self-service password reset.
///
/// Codes are stored as PBKDF2 hashes, never in the clear: the table sits in the same database as
/// everything else, and a readable 6-digit code would be a password-equivalent. One row per
/// account (upserted), so asking for a new code silently invalidates the previous one. Rows are
/// deleted the moment they are used, expire, or burn through their attempt budget.
/// </summary>
public sealed class OtpStore
{
    private readonly string _connectionString;
    private readonly int _maxAttempts;

    public OtpStore(string dbPath, int maxAttempts = 5)
    {
        _connectionString = new SqliteConnectionStringBuilder
        { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        _maxAttempts = maxAttempts;
        Initialize();
    }

    private SqliteConnection Open() { var c = new SqliteConnection(_connectionString); c.Open(); return c; }

    private void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS password_otps (
                username    TEXT PRIMARY KEY COLLATE NOCASE,
                code_hash   TEXT NOT NULL,
                expires_utc TEXT NOT NULL,
                attempts    INTEGER NOT NULL DEFAULT 0,
                issued_utc  TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Issue a code, replacing any outstanding one for this account.</summary>
    public void Issue(string username, string codeHash, DateTimeOffset expiresUtc)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO password_otps (username, code_hash, expires_utc, attempts, issued_utc)
            VALUES ($u, $h, $e, 0, $i)
            ON CONFLICT(username) DO UPDATE SET
                code_hash = $h, expires_utc = $e, attempts = 0, issued_utc = $i;
            """;
        cmd.Parameters.AddWithValue("$u", username);
        cmd.Parameters.AddWithValue("$h", codeHash);
        cmd.Parameters.AddWithValue("$e", expiresUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$i", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Verifies a code and, on success, consumes it so it cannot be replayed. A wrong code costs
    /// one attempt from the budget; exhausting the budget destroys the code entirely, so an
    /// attacker gets a handful of guesses at a 6-digit value rather than a million.
    /// </summary>
    public OtpResult TryConsume(string username, string code)
    {
        using var conn = Open();

        string hash;
        DateTimeOffset expires;
        int attempts;
        using (var read = conn.CreateCommand())
        {
            read.CommandText = "SELECT code_hash, expires_utc, attempts FROM password_otps WHERE username = $u;";
            read.Parameters.AddWithValue("$u", username);
            using var r = read.ExecuteReader();
            if (!r.Read())
                return OtpResult.NoneOutstanding;
            hash = r.GetString(0);
            expires = DateTimeOffset.Parse(r.GetString(1));
            attempts = r.GetInt32(2);
        }

        if (DateTimeOffset.UtcNow > expires)
        {
            Clear(username);
            return OtpResult.Expired;
        }

        if (attempts >= _maxAttempts)
        {
            Clear(username);
            return OtpResult.TooManyAttempts;
        }

        // Constant-time comparison lives inside PasswordHasher.Verify.
        if (!PasswordHasher.Verify(code, hash))
        {
            using var bump = conn.CreateCommand();
            bump.CommandText = "UPDATE password_otps SET attempts = attempts + 1 WHERE username = $u;";
            bump.Parameters.AddWithValue("$u", username);
            bump.ExecuteNonQuery();

            if (attempts + 1 >= _maxAttempts)
            {
                Clear(username);
                return OtpResult.TooManyAttempts;
            }
            return OtpResult.WrongCode;
        }

        Clear(username);   // single use
        return OtpResult.Ok;
    }

    public void Clear(string username)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM password_otps WHERE username = $u;";
        cmd.Parameters.AddWithValue("$u", username);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Housekeeping: drop codes that aged out without being used.</summary>
    public int PurgeExpired()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM password_otps WHERE expires_utc < $now;";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o"));
        return cmd.ExecuteNonQuery();
    }
}
