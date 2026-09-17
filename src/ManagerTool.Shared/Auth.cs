namespace ManagerTool.Shared;

/// <summary>Role claim values carried in the JWT.</summary>
public static class Roles
{
    public const string Admin = "admin";
    public const string Agent = "agent";
}

/// <summary>Admin console login request.</summary>
public sealed record LoginRequest(string Username, string Password);

/// <summary>Agent enrollment request — the pre-shared enrollment key plus who is asking.</summary>
public sealed record AgentEnrollRequest(string EnrollmentKey, string HostId, string MachineName);

/// <summary>Issued token + its UTC expiry.</summary>
public sealed record TokenResponse(string AccessToken, DateTimeOffset ExpiresAtUtc);

/// <summary>Self-service password change: verify current, set new.</summary>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>
/// Owner action: issue a viewer account outright, with credentials the owner chooses and hands
/// over. Skips the request/approve round trip — the account is usable immediately.
/// </summary>
public sealed record CreateAdminRequest(string Username, string Password);

/// <summary>
/// Anonymous "I forgot my password" request. Username only — this route can never create an
/// account or set a credential, and it answers identically whether or not the account exists,
/// so it cannot be used to enumerate logins. The owner performs the actual reset.
/// </summary>
public sealed record PasswordResetRequest(string Username);

/// <summary>A lockout report waiting for the owner to action or dismiss it.</summary>
public sealed record PasswordResetTicket(string Username, DateTimeOffset RequestedUtc);

/// <summary>
/// Completes a self-service reset: the one-time code that was emailed, plus the replacement
/// password. Answers generically on any failure so a caller cannot probe which part was wrong.
/// </summary>
public sealed record OtpResetRequest(string Username, string Code, string NewPassword);

/// <summary>
/// Tells the login screen whether to show the code box or the "ask the owner" message.
///
/// <para><b>Carries no information about the account.</b> <see cref="CodeSent"/> is computed from
/// only two things the caller can already determine for themselves: whether the submitted
/// username is email-shaped, and whether the server has SMTP configured at all. An unknown
/// username gets exactly the same answer as a real one — the difference is simply that no mail
/// is sent and no code will ever verify.</para>
/// </summary>
public sealed record ForgotPasswordResult(bool CodeSent);
