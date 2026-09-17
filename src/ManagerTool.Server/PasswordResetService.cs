using System.Security.Cryptography;
using ManagerTool.Shared;

namespace ManagerTool.Server;

/// <summary>Tunables for self-service reset (ManagerTool:PasswordReset).</summary>
public sealed class PasswordResetOptions
{
    /// <summary>How long an emailed code stays valid.</summary>
    public int CodeMinutes { get; set; } = 10;

    /// <summary>Wrong guesses allowed before the code is destroyed.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Digits in the emailed code.</summary>
    public int CodeDigits { get; set; } = 6;
}

/// <summary>
/// Self-service password reset by emailed one-time code.
///
/// <para>Two rules shape everything here. First, <b>nothing may reveal whether an account
/// exists</b> — every path returns the same shape, and the decision to show a code box is made
/// from the submitted text and server config alone. Second, <b>the code only ever goes to the
/// account's own address</b>: the username IS the destination, so a caller cannot redirect a
/// reset to an inbox they control.</para>
///
/// <para>Owners are supported deliberately. They are seeded from configuration and so had no way
/// back in if they forgot their password; a successful reset writes a database override, which
/// the token service already prefers over the config seed while leaving owner rights intact.</para>
/// </summary>
public sealed class PasswordResetService
{
    private readonly PasswordResetOptions _opts;
    private readonly OtpStore _otps;
    private readonly TokenService _tokens;
    private readonly AdminCredentialStore _credentials;
    private readonly NotificationService _mail;
    private readonly PasswordResetStore _tickets;
    private readonly AuditStore _audit;

    public PasswordResetService(PasswordResetOptions opts, OtpStore otps, TokenService tokens,
        AdminCredentialStore credentials, NotificationService mail, PasswordResetStore tickets,
        AuditStore audit)
    {
        _opts = opts;
        _otps = otps;
        _tokens = tokens;
        _credentials = credentials;
        _mail = mail;
        _tickets = tickets;
        _audit = audit;
    }

    /// <summary>
    /// Whether a code could be emailed for this input. Computed from the username's shape and the
    /// server's SMTP config only — never from whether the account exists — so the answer is safe
    /// to return to an anonymous caller.
    /// </summary>
    public bool CanEmailCodeFor(string username) =>
        _mail.CanSendMail && LooksLikeEmail(username);

    /// <summary>
    /// Best-effort: if the account is real, issue a code and email it. Returns nothing meaningful
    /// on purpose — the caller answers identically either way.
    /// </summary>
    public async Task BeginAsync(string username)
    {
        username = username.Trim();
        if (!CanEmailCodeFor(username) || !AccountExists(username))
            return;

        var code = GenerateCode(_opts.CodeDigits);
        var expires = DateTimeOffset.UtcNow.AddMinutes(_opts.CodeMinutes);
        _otps.Issue(username, PasswordHasher.Hash(code), expires);

        var sent = await _mail.TrySendMailAsync(
            username,
            "Manager Tool — your password reset code",
            $"""
             Someone asked to reset the Manager Tool password for this account.

             Your code is: {code}

             It expires in {_opts.CodeMinutes} minutes and can be used once. You have
             {_opts.MaxAttempts} attempts to enter it.

             If this wasn't you, ignore this email — your password has not changed. Nobody can
             reset it without this code.
             """);

        if (sent)
        {
            _audit.Log(username, "password-reset-code", null, "Emailed a reset code");
            return;
        }

        // Delivery failed — bad app password, SMTP down, mailbox rejected it. The response has
        // already told the caller to expect a code, so without this they would sit waiting for
        // mail that will never arrive. Drop the code first: nobody received it, so leaving it live
        // would be a credential outstanding for no reason.
        _otps.Clear(username);

        // Fall back to the owner's queue — but never for an owner themselves. A ticket only the
        // owner can action is no use to a locked-out owner; for them this stays a server-side fix,
        // which is exactly why the queue excluded owners in the first place.
        if (_tokens.IsOwner(username))
        {
            _audit.Log(username, "password-reset-code", null,
                "Owner reset code could not be emailed — needs a server-side fix");
            return;
        }

        _tickets.Record(username);
        _audit.Log(username, "password-reset-code", null,
            "Reset code could not be emailed — queued for the owner instead");
    }

    /// <summary>
    /// Verifies the code and sets the new password. Every failure returns false with no detail —
    /// the reason is written to the audit log instead.
    /// </summary>
    public bool Complete(string username, string code, string newPassword)
    {
        username = username.Trim();
        if (newPassword is null || newPassword.Length < 8)
            return false;
        if (!AccountExists(username))
        {
            // Burn an attempt anyway so a missing account is not faster to probe than a real one.
            _otps.TryConsume(username, code);
            return false;
        }

        var result = _otps.TryConsume(username, code);
        if (result != OtpResult.Ok)
        {
            _audit.Log(username, "password-reset-failed", null, $"Reset code rejected ({result})");
            return false;
        }

        // Works for owners too: this override is what the token service reads first.
        _credentials.Upsert(username, PasswordHasher.Hash(newPassword), AdminStatus.Active);
        _tickets.Clear(username);   // any owner-visible lockout report is now moot
        _audit.Log(username, "password-reset-complete", null, "Password reset using an emailed code");
        return true;
    }

    private bool AccountExists(string username) =>
        _tokens.ListAdmins().Any(a => string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Cheap shape check — enough to decide whether the username can act as a destination. Real
    /// validation is irrelevant: if it isn't a working mailbox the code simply never arrives.
    /// </summary>
    private static bool LooksLikeEmail(string value)
    {
        var at = value.IndexOf('@');
        if (at <= 0 || at == value.Length - 1) return false;
        var domain = value[(at + 1)..];
        return !value.Contains(' ') && domain.Contains('.') && !domain.StartsWith('.') && !domain.EndsWith('.');
    }

    /// <summary>Uniformly-distributed numeric code (no modulo bias).</summary>
    private static string GenerateCode(int digits)
    {
        var chars = new char[digits];
        for (var i = 0; i < digits; i++)
            chars[i] = (char)('0' + RandomNumberGenerator.GetInt32(10));
        return new string(chars);
    }
}
