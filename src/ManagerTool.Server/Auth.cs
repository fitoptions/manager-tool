using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using ManagerTool.Shared;

namespace ManagerTool.Server;

/// <summary>Strongly-typed view of the "Auth" config section.</summary>
public sealed class AuthOptions
{
    public string Issuer { get; set; } = "ManagerTool";
    public string Audience { get; set; } = "ManagerTool";
    public string SigningKey { get; set; } = "";           // >= 32 bytes; REQUIRED in prod
    public int AdminTokenMinutes { get; set; } = 60;
    public int AgentTokenDays { get; set; } = 30;
    public string AgentEnrollmentKey { get; set; } = "";   // pre-shared secret handed to agents
    public List<AdminAccount> Admins { get; set; } = new();
}

/// <summary>One admin account as the console lists it. Owners are the config-seeded accounts.</summary>
public sealed record AdminSummary(string Username, string Status, bool IsOwner);

/// <summary>An admin login, stored as a PBKDF2 hash — never a plaintext password.</summary>
public sealed class AdminAccount
{
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";  // format: pbkdf2$<iters>$<saltB64>$<hashB64>
}

/// <summary>Verifies credentials and mints signed JWTs for both roles.</summary>
public sealed class TokenService
{
    private readonly AuthOptions _opts;
    private readonly AdminCredentialStore _credentials;
    private readonly SymmetricSecurityKey _key;

    public TokenService(AuthOptions opts, AdminCredentialStore credentials)
    {
        _opts = opts;
        _credentials = credentials;
        if (Encoding.UTF8.GetByteCount(opts.SigningKey) < 32)
            throw new InvalidOperationException(
                "Auth:SigningKey must be at least 32 bytes. Set a strong random value in configuration.");
        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(opts.SigningKey));
    }

    private AdminAccount? ConfigAdmin(string username) =>
        _opts.Admins.FirstOrDefault(a => string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase));

    /// <summary>Config-seeded admins are the owners: they may approve/remove other admins.</summary>
    public bool IsOwner(string username) => ConfigAdmin(username) is not null;

    /// <summary>Active password hash: a DB override / approved admin, else the config owner seed.</summary>
    private string? ActiveHashFor(string username) =>
        _credentials.GetActiveHash(username) ?? ConfigAdmin(username)?.PasswordHash;

    public TokenResponse? TryLoginAdmin(LoginRequest req)
    {
        var hash = ActiveHashFor(req.Username);
        if (hash is null || !PasswordHasher.Verify(req.Password, hash))
            return null;

        return Issue(req.Username, Roles.Admin, TimeSpan.FromMinutes(_opts.AdminTokenMinutes));
    }

    /// <summary>
    /// Verifies the current password and, on success, persists a new hash for the user.
    /// Works for both owners (config-seeded) and approved admins.
    /// </summary>
    public bool ChangePassword(string username, string currentPassword, string newPassword)
    {
        var hash = ActiveHashFor(username);
        if (hash is null || !PasswordHasher.Verify(currentPassword, hash))
            return false;
        _credentials.Upsert(username, PasswordHasher.Hash(newPassword), AdminStatus.Active);
        return true;
    }

    /// <summary>
    /// Owner action: create a viewer account with owner-chosen credentials, active immediately —
    /// the owner vouching for the account IS the approval. Refuses a name already held by an
    /// owner or by an existing/pending account, so this can never silently reset someone's login.
    /// </summary>
    public bool CreateAdmin(string username, string password)
    {
        username = username?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(username) || password is null || password.Length < 8)
            return false;
        if (ConfigAdmin(username) is not null || _credentials.Exists(username))
            return false;
        _credentials.Upsert(username, PasswordHasher.Hash(password), AdminStatus.Active);
        return true;
    }

    /// <summary>All admins: config owners plus managed accounts, with status + owner flag.</summary>
    public IReadOnlyList<AdminSummary> ListAdmins()
    {
        var owners = _opts.Admins.Select(a => new AdminSummary(a.Username, AdminStatus.Active, true));
        var managed = _credentials.List()
            .Where(u => ConfigAdmin(u.Username) is null)   // don't double-list an owner
            .Select(u => new AdminSummary(u.Username, u.Status, false));
        return owners.Concat(managed).ToList();
    }

    public bool ApproveAdmin(string username)
    {
        var u = _credentials.Get(username);
        return u is not null && _credentials.SetStatus(username, AdminStatus.Active);
    }

    /// <summary>
    /// Owner action: force a new password on a managed admin and activate them. Cannot target
    /// an owner (config-seeded) account — owners rotate their own password via change-password.
    /// </summary>
    public bool ResetAdminPassword(string username, string newPassword)
    {
        if (ConfigAdmin(username) is not null || !_credentials.Exists(username) || newPassword.Length < 8)
            return false;
        _credentials.Upsert(username, PasswordHasher.Hash(newPassword), AdminStatus.Active);
        return true;
    }

    /// <summary>Remove a managed admin. Owners (config-seeded) cannot be removed here.</summary>
    public bool RemoveAdmin(string username) =>
        ConfigAdmin(username) is null && _credentials.Delete(username);

    public TokenResponse? TryEnrollAgent(AgentEnrollRequest req)
    {
        // Constant-time compare of the pre-shared enrollment key.
        if (string.IsNullOrEmpty(_opts.AgentEnrollmentKey) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(req.EnrollmentKey),
                Encoding.UTF8.GetBytes(_opts.AgentEnrollmentKey)))
            return null;

        return Issue(req.HostId, Roles.Agent, TimeSpan.FromDays(_opts.AgentTokenDays));
    }

    private TokenResponse Issue(string subject, string role, TimeSpan lifetime)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now.Add(lifetime);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, subject),
            new Claim(ClaimTypes.Role, role),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };

        var creds = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: _opts.Issuer,
            audience: _opts.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: creds);

        var jwt = new JwtSecurityTokenHandler().WriteToken(token);
        return new TokenResponse(jwt, expires);
    }

    public TokenValidationParameters ValidationParameters => new()
    {
        ValidateIssuer = true,
        ValidIssuer = _opts.Issuer,
        ValidateAudience = true,
        ValidAudience = _opts.Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = _key,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
    };
}

/// <summary>PBKDF2 (SHA-256) password hashing with a stored per-password salt.</summary>
public static class PasswordHasher
{
    private const int Iterations = 100_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2")
            return false;
        if (!int.TryParse(parts[1], out var iters))
            return false;

        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iters, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
