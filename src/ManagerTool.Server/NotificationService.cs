using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using ManagerTool.Shared;

namespace ManagerTool.Server;

/// <summary>Config for outbound alert delivery (ManagerTool:Notifications).</summary>
public sealed class NotificationOptions
{
    /// <summary>Only alerts at or above this severity are delivered. info | warning | critical.</summary>
    public string MinSeverity { get; set; } = Severities.Warning;

    public EmailOptions? Email { get; set; }
    public string? SlackWebhookUrl { get; set; }
    public string? TeamsWebhookUrl { get; set; }
    public string? WebhookUrl { get; set; }   // generic JSON POST of the alert
}

public sealed class EmailOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string? User { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "";
    public List<string> To { get; set; } = new();
}

/// <summary>
/// Fans a fired alert out to the configured channels (email / Slack / Teams / generic webhook).
/// Delivery is best-effort and never blocks the hot activity path: each channel is dispatched
/// on a background task with its own try/catch. Alerts below <see cref="NotificationOptions.MinSeverity"/>
/// are dropped so low-value "info" noise does not page anyone.
/// </summary>
public sealed class NotificationService
{
    private readonly NotificationOptions _opts;
    private readonly ILogger<NotificationService> _logger;
    private readonly IHttpClientFactory _httpFactory;

    public NotificationService(NotificationOptions opts, IHttpClientFactory httpFactory,
        ILogger<NotificationService> logger)
    {
        _opts = opts;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    private static int Rank(string severity) => severity switch
    {
        Severities.Critical => 2,
        Severities.Warning => 1,
        _ => 0,
    };

    /// <summary>Returns true if this alert meets the configured minimum severity.</summary>
    public bool ShouldDeliver(Alert alert) => Rank(alert.Severity) >= Rank(_opts.MinSeverity);

    /// <summary>
    /// True when enough SMTP settings exist to send at all. Alert delivery additionally needs a
    /// configured recipient list; a password-reset code supplies its own recipient, so it only
    /// needs a host and a From address.
    /// </summary>
    public bool CanSendMail => _opts.Email is { Host.Length: > 0, From.Length: > 0 };

    /// <summary>
    /// Sends one message to a single address. Used for password-reset codes, which must go to the
    /// account holder rather than the alert distribution list. Awaited (not fire-and-forget) so
    /// the caller can log a delivery failure — though it still never reports that failure to the
    /// requester, since "mail bounced" would confirm the account exists.
    /// </summary>
    public async Task<bool> TrySendMailAsync(string toAddress, string subject, string body)
    {
        if (!CanSendMail)
            return false;

        try
        {
            var e = _opts.Email!;
            using var msg = new MailMessage
            { From = new MailAddress(e.From), Subject = subject, Body = body };
            msg.To.Add(toAddress);

            using var client = new SmtpClient(e.Host, e.Port) { EnableSsl = e.UseSsl };
            if (!string.IsNullOrEmpty(e.User))
                client.Credentials = new NetworkCredential(e.User, e.Password);
            await client.SendMailAsync(msg);
            return true;
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: the endpoint's response must not vary with mail outcome.
            _logger.LogError(ex, "Password-reset mail to {Recipient} failed", toAddress);
            return false;
        }
    }

    /// <summary>Fire-and-forget dispatch to all configured channels.</summary>
    public void Notify(Alert alert)
    {
        if (!ShouldDeliver(alert))
            return;

        if (_opts.Email is { Host.Length: > 0, To.Count: > 0 })
            _ = SafeSend("email", () => SendEmailAsync(alert));
        if (!string.IsNullOrWhiteSpace(_opts.SlackWebhookUrl))
            _ = SafeSend("slack", () => PostJsonAsync(_opts.SlackWebhookUrl!, new { text = SlackTeamsText(alert) }));
        if (!string.IsNullOrWhiteSpace(_opts.TeamsWebhookUrl))
            _ = SafeSend("teams", () => PostJsonAsync(_opts.TeamsWebhookUrl!, new { text = SlackTeamsText(alert) }));
        if (!string.IsNullOrWhiteSpace(_opts.WebhookUrl))
            _ = SafeSend("webhook", () => PostJsonAsync(_opts.WebhookUrl!, alert));
    }

    private async Task SafeSend(string channel, Func<Task> send)
    {
        try { await send(); }
        catch (Exception ex) { _logger.LogError(ex, "Alert delivery to {Channel} failed", channel); }
    }

    private async Task PostJsonAsync<T>(string url, T payload)
    {
        using var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(10);
        using var resp = await http.PostAsJsonAsync(url, payload);
        resp.EnsureSuccessStatusCode();
    }

    private async Task SendEmailAsync(Alert alert)
    {
        var e = _opts.Email!;
        using var msg = new MailMessage { From = new MailAddress(e.From), Subject = EmailSubject(alert), Body = EmailBody(alert) };
        foreach (var to in e.To)
            msg.To.Add(to);

        using var client = new SmtpClient(e.Host, e.Port) { EnableSsl = e.UseSsl };
        if (!string.IsNullOrEmpty(e.User))
            client.Credentials = new NetworkCredential(e.User, e.Password);
        await client.SendMailAsync(msg);
    }

    // ---- message formatting ----

    private static string SlackTeamsText(Alert a) =>
        $"{Icon(a.Severity)} *Manager Tool {a.Severity.ToUpperInvariant()}* — {a.MachineName}\n{a.Message}\n_{a.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}_";

    private static string EmailSubject(Alert a) =>
        $"[Manager Tool {a.Severity.ToUpperInvariant()}] {a.MachineName}: {a.RuleName}";

    private static string EmailBody(Alert a) =>
        $"""
        A Manager Tool compliance rule fired.

        Severity : {a.Severity.ToUpperInvariant()}
        Machine  : {a.MachineName}
        Rule     : {a.RuleName}
        Detail   : {a.Message}
        App      : {a.ProcessName}
        Window   : {a.WindowTitle}
        URL      : {a.Url ?? "(none)"}
        Time     : {a.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}
        """;

    private static string Icon(string severity) => severity switch
    {
        Severities.Critical => "🔴",
        Severities.Warning => "🟠",
        _ => "🔵",
    };
}
