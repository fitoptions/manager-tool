using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using ManagerTool.Server;
using ManagerTool.Shared;

// --- CLI utility mode: generate a PBKDF2 hash for an admin password ---
//     Usage:  dotnet run --project src/ManagerTool.Server -- hash-password "S3cret!"
if (args.Length >= 2 && args[0] == "hash-password")
{
    Console.WriteLine(PasswordHasher.Hash(args[1]));
    return;
}

var builder = WebApplication.CreateBuilder(args);

// Allows the server to run as a Windows Service (auto-start on boot). No-ops when
// launched from the console, so `dotnet run` still works for dev.
builder.Host.UseWindowsService();

// ---- Auth configuration ----
var authOptions = builder.Configuration.GetSection("Auth").Get<AuthOptions>()
                  ?? throw new InvalidOperationException("Missing 'Auth' configuration section.");
var dbPath = builder.Configuration["ManagerTool:DbPath"]
             ?? Path.Combine(AppContext.BaseDirectory, "managertool.db");
var adminCredentials = new AdminCredentialStore(dbPath);
var tokenService = new TokenService(authOptions, adminCredentials);
builder.Services.AddSingleton(authOptions);
builder.Services.AddSingleton(adminCredentials);
builder.Services.AddSingleton(tokenService);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = tokenService.ValidationParameters;

        // SignalR delivers the token via the query string on the WebSocket handshake,
        // because browsers/clients can't set Authorization headers on WS. Accept it there
        // only for the hub path.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var accessToken = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) &&
                    ctx.HttpContext.Request.Path.StartsWithSegments(HubRoutes.Path))
                {
                    ctx.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", p => p.RequireRole(Roles.Admin));
    options.AddPolicy("AgentOnly", p => p.RequireRole(Roles.Agent));
});

builder.Services.AddSignalR(o =>
{
    o.MaximumReceiveMessageSize = 8 * 1024 * 1024;
});

// Throttles the one anonymous route (forgotten-password reports) as basic hygiene against log and
// notification noise. Deliberately a single generous bucket, not per-username or per-IP: the
// username lives in the request BODY, and partitioning by it would mean putting a login name in the
// query string where nginx would log it. Per-IP is no better — behind the proxy every request
// arrives from loopback. The real containment is in the route itself: it accepts no credential,
// only flags accounts that already exist, and upserts, so the queue cannot grow past one row per
// account no matter how often it is called. A genuine user needs one call.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Issuing codes / queuing lockout reports.
    options.AddFixedWindowLimiter("reset-request", limiter =>
    {
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.PermitLimit = 20;
        limiter.QueueLimit = 0;
    });

    // Submitting a code is a SEPARATE bucket. Sharing one with the route above would let a burst
    // of wrong guesses exhaust the budget and block everyone from *requesting* a code — locking
    // out the very people the feature exists for. Brute force is not what this bound is for
    // anyway: each code dies after 5 wrong attempts, so guessing is capped per code regardless.
    options.AddFixedWindowLimiter("reset-verify", limiter =>
    {
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.PermitLimit = 40;
        limiter.QueueLimit = 0;
    });
});

builder.Services.AddSingleton(new ActivityStore(dbPath));
builder.Services.AddSingleton(new AlertStore(dbPath));
builder.Services.AddSingleton(new AuditStore(dbPath));
builder.Services.AddSingleton(new CategoryStore(dbPath));
builder.Services.AddSingleton(new SettingsStore(dbPath));
builder.Services.AddSingleton(new HostLabelStore(dbPath));
builder.Services.AddSingleton(new UpdateStore(dbPath));
builder.Services.AddSingleton<UpdateCoordinator>();
builder.Services.AddSingleton(new PasswordResetStore(dbPath));

// ---- Self-service password reset by emailed one-time code ----
var resetOptions = builder.Configuration.GetSection("ManagerTool:PasswordReset").Get<PasswordResetOptions>()
                   ?? new PasswordResetOptions();
builder.Services.AddSingleton(resetOptions);
builder.Services.AddSingleton(new OtpStore(dbPath, resetOptions.MaxAttempts));
builder.Services.AddSingleton<PasswordResetService>();
builder.Services.AddSingleton(new HostAccessStore(dbPath));
builder.Services.AddSingleton<HostAccess>();
builder.Services.AddSingleton<RuleEngine>();
builder.Services.AddSingleton<RecordingLibrary>();
builder.Services.AddSingleton<HostRegistry>();
builder.Services.AddSingleton<FrameRelay>();
builder.Services.AddSingleton<AdminRegistry>();

// ---- Alert delivery (email / Slack / Teams / webhook) ----
var notificationOptions = builder.Configuration.GetSection("ManagerTool:Notifications").Get<NotificationOptions>()
                          ?? new NotificationOptions();
builder.Services.AddSingleton(notificationOptions);
builder.Services.AddHttpClient();
builder.Services.AddSingleton<NotificationService>();

// ---- On-disk session recording ----
var recordingOptions = builder.Configuration.GetSection("ManagerTool:Recording").Get<RecordingOptions>()
                       ?? new RecordingOptions();
builder.Services.AddSingleton(recordingOptions);
builder.Services.AddSingleton<Recorder>();

// ---- Retention policy (activity + recordings) ----
var retentionOptions = builder.Configuration.GetSection("ManagerTool:Retention").Get<RetentionOptions>()
                       ?? new RetentionOptions();
builder.Services.AddSingleton(retentionOptions);
builder.Services.AddHostedService<RetentionService>();

var app = builder.Build();

// Serve the web admin dashboard (wwwroot/index.html) at the site root. The SPA assets are
// public; the data APIs and hub it calls remain JWT-protected below.
// No-cache on the dashboard assets so a redeploy is picked up on the next load — no hard-refresh.
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var headers = ctx.Context.Response.Headers;
        headers.CacheControl = "no-cache, no-store, must-revalidate";
        headers.Pragma = "no-cache";
        headers.Expires = "0";
    }
});

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapHub<MonitorHub>(HubRoutes.Path).RequireAuthorization();

// ---- Auth endpoints (anonymous) ----

app.MapPost("/api/auth/login", (LoginRequest req, TokenService tokens, AuditStore audit) =>
{
    var result = tokens.TryLoginAdmin(req);
    if (result is null) return Results.Unauthorized();
    audit.Log(req.Username, AuditActions.Login, null, "Admin signed in");
    return Results.Ok(result);
});

app.MapPost("/api/auth/enroll", (AgentEnrollRequest req, TokenService tokens) =>
{
    var result = tokens.TryEnrollAgent(req);
    return result is null ? Results.Unauthorized() : Results.Ok(result);
});

// NOTE: there is deliberately no public self-registration route. Accounts exist only because the
// owner issued them (POST /api/admins). Any legacy 'pending' rows can still be approved or removed
// from the admin list, but no new one can be created from outside.

// Start of a forgotten-password recovery. The only anonymous write route, and deliberately inert:
// it accepts no password, cannot create an account, and answers identically for every input — so
// it reveals nothing about which usernames exist.
//
// Where the username is email-shaped and SMTP is configured, a one-time code is emailed to THAT
// address (the username is the destination; a caller cannot redirect it). Otherwise it falls back
// to queuing a lockout report for the owner to action by hand.
app.MapPost("/api/auth/reset-request",
    async (PasswordResetRequest req, TokenService tokens, PasswordResetStore resets,
           PasswordResetService recovery, AuditStore audit) =>
    {
        var username = req.Username?.Trim() ?? "";

        // Depends only on the submitted text + server config, never on the account store.
        var byEmail = username.Length > 0 && recovery.CanEmailCodeFor(username);

        if (byEmail)
        {
            await recovery.BeginAsync(username);      // no-ops silently for an unknown account
        }
        else
        {
            // Owners are config-seeded, so a forgotten owner password must not sit in a queue only
            // that owner could action — it stays a server-side fix.
            var known = username.Length > 0
                        && !tokens.IsOwner(username)
                        && tokens.ListAdmins().Any(a =>
                               string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase));
            if (known)
            {
                resets.Record(username);
                audit.Log(username, "password-reset-request", null, "Reported being locked out");
            }
        }

        // Tells the UI which box to show. Carries nothing about the account — see ForgotPasswordResult.
        return Results.Ok(new ForgotPasswordResult(byEmail));
    }).RequireRateLimiting("reset-request");

// Completes the emailed-code reset. One generic failure message: distinguishing "no code
// outstanding" from "wrong code" would confirm which accounts have a reset in flight.
app.MapPost("/api/auth/reset-password",
    (OtpResetRequest req, PasswordResetService recovery) =>
    {
        if (req.NewPassword is null || req.NewPassword.Length < 8)
            return Results.BadRequest("New password must be at least 8 characters.");

        return recovery.Complete(req.Username ?? "", req.Code ?? "", req.NewPassword)
            ? Results.NoContent()
            : Results.BadRequest("That code is invalid or has expired. Request a new one.");
    }).RequireRateLimiting("reset-verify");

// ---- Forgotten-password queue (owner only) ----

app.MapGet("/api/admins/reset-requests",
    (TokenService tokens, PasswordResetStore resets, System.Security.Claims.ClaimsPrincipal user) =>
        tokens.IsOwner(Actor(user)) ? Results.Ok(resets.List()) : Results.Forbid())
    .RequireAuthorization("AdminOnly");

app.MapDelete("/api/admins/reset-requests/{username}",
    (string username, TokenService tokens, PasswordResetStore resets, AuditStore audit,
     System.Security.Claims.ClaimsPrincipal user) =>
    {
        if (!tokens.IsOwner(Actor(user))) return Results.Forbid();
        if (!resets.Clear(username)) return Results.NotFound();
        audit.Log(Actor(user), "password-reset-dismiss", null, $"Dismissed reset request from '{username}'");
        return Results.NoContent();
    }).RequireAuthorization("AdminOnly");

// Self-service password change (any signed-in admin changes their own).
app.MapPost("/api/auth/change-password",
    (ChangePasswordRequest req, TokenService tokens, AuditStore audit,
     System.Security.Claims.ClaimsPrincipal user) =>
    {
        if (req.NewPassword is null || req.NewPassword.Length < 8)
            return Results.BadRequest("New password must be at least 8 characters.");
        var me = Actor(user);
        if (!tokens.ChangePassword(me, req.CurrentPassword, req.NewPassword))
            return Results.BadRequest("Current password is incorrect.");
        audit.Log(me, "password-change", null, "Changed own password");
        return Results.NoContent();
    }).RequireAuthorization("AdminOnly");

// ---- Admin management (owner only) ----

app.MapGet("/api/admins", (TokenService tokens, System.Security.Claims.ClaimsPrincipal user) =>
        tokens.IsOwner(Actor(user)) ? Results.Ok(tokens.ListAdmins()) : Results.Forbid())
    .RequireAuthorization("AdminOnly");

// Owner issues a viewer account directly — no request/approve round trip. The owner hands the
// credentials over out of band; the new account is active at once.
app.MapPost("/api/admins",
    (CreateAdminRequest req, TokenService tokens, AuditStore audit, System.Security.Claims.ClaimsPrincipal user) =>
    {
        if (!tokens.IsOwner(Actor(user))) return Results.Forbid();
        if (string.IsNullOrWhiteSpace(req.Username)) return Results.BadRequest("Username is required.");
        if (req.Password is null || req.Password.Length < 8)
            return Results.BadRequest("Password must be at least 8 characters.");
        if (!tokens.CreateAdmin(req.Username, req.Password))
            return Results.Conflict("That username is already taken.");

        var created = req.Username.Trim();
        audit.Log(Actor(user), "admin-create", null, $"Issued viewer account '{created}'");
        return Results.Ok(new { username = created, status = AdminStatus.Active });
    }).RequireAuthorization("AdminOnly");

app.MapPost("/api/admins/{username}/approve",
    (string username, TokenService tokens, AuditStore audit, System.Security.Claims.ClaimsPrincipal user) =>
    {
        if (!tokens.IsOwner(Actor(user))) return Results.Forbid();
        if (!tokens.ApproveAdmin(username)) return Results.NotFound();
        audit.Log(Actor(user), "admin-approve", null, $"Approved admin '{username}'");
        return Results.NoContent();
    }).RequireAuthorization("AdminOnly");

app.MapDelete("/api/admins/{username}",
    (string username, TokenService tokens, HostAccessStore hostAccess, PasswordResetStore resets,
     AuditStore audit, System.Security.Claims.ClaimsPrincipal user) =>
    {
        if (!tokens.IsOwner(Actor(user))) return Results.Forbid();
        if (!tokens.RemoveAdmin(username)) return Results.NotFound();
        hostAccess.Clear(username);   // don't leave orphan blocks to be inherited by a re-used name
        resets.Clear(username);       // nor a ticket for an account that no longer exists
        audit.Log(Actor(user), "admin-remove", null, $"Removed admin '{username}'");
        return Results.NoContent();
    }).RequireAuthorization("AdminOnly");

// ---- Viewer workstation access (owner only) ----
// Deny-list model: a viewer with no blocks sees every PC, including ones enrolled later.

app.MapGet("/api/access",
    (TokenService tokens, HostAccessStore hostAccess, HostLabelStore hostDirectory,
     HostRegistry registry, System.Security.Claims.ClaimsPrincipal user) =>
    {
        if (!tokens.IsOwner(Actor(user))) return Results.Forbid();

        var online = registry.Connected.Select(h => h.HostId).ToHashSet();
        var nicknames = hostDirectory.GetAll();
        var hosts = hostDirectory.KnownHosts()
            .Select(h => new AccessHost(
                h.HostId,
                h.MachineName,
                nicknames.TryGetValue(h.HostId, out var n) ? n : null,
                h.LastSeenUtc,
                online.Contains(h.HostId)))
            .ToArray();

        var viewers = tokens.ListAdmins()
            .Where(a => !a.IsOwner)
            .Select(a => new AccessViewer(a.Username, a.Status, hostAccess.DeniedFor(a.Username).ToArray()))
            .ToArray();

        return Results.Ok(new AccessMatrix(hosts, viewers));
    }).RequireAuthorization("AdminOnly");

app.MapPut("/api/access/{username}",
    async (string username, HostAccessInput input, TokenService tokens, HostAccessStore hostAccess,
     AdminRegistry admins, HostRegistry registry, IHubContext<MonitorHub> hub,
     AuditStore audit, System.Security.Claims.ClaimsPrincipal user) =>
    {
        if (!tokens.IsOwner(Actor(user))) return Results.Forbid();
        if (tokens.IsOwner(username)) return Results.BadRequest("Owner accounts always see every workstation.");
        if (!tokens.ListAdmins().Any(a => string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase)))
            return Results.NotFound();

        var denied = (input.DeniedHostIds ?? Array.Empty<string>())
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        hostAccess.SetDenied(username, denied);

        // Revocation must bite immediately: a viewer already watching a now-blocked PC is dropped
        // from its frame group here, rather than keeping the stream until they happen to disconnect.
        foreach (var connectionId in admins.ConnectionsFor(username))
        {
            foreach (var hostId in denied)
            {
                await hub.Groups.RemoveFromGroupAsync(connectionId, HubRoutes.ViewerGroup(hostId));
                if (registry.ReleaseHold(hostId, connectionId))
                    await hub.Clients.Group(HubRoutes.HostGroup(hostId)).SendAsync(nameof(IAgentClient.EndScreenStream));
            }
            await hub.Clients.Client(connectionId).SendAsync(nameof(IAdminClient.AccessChanged), denied);
        }

        audit.Log(Actor(user), AuditActions.AccessChange, null, denied.Length == 0
            ? $"Gave '{username}' access to all workstations"
            : $"Blocked {denied.Length} workstation(s) for '{username}'");
        return Results.NoContent();
    }).RequireAuthorization("AdminOnly");

// ---- Admin query API (admin JWT required) ----
// Every route below that names a host is scoped by HostAccess: a blocked workstation is
// absent from lists and rejected by id, so it is invisible rather than merely un-clickable.

app.MapGet("/api/hosts", (HostRegistry registry, HostAccess access, System.Security.Claims.ClaimsPrincipal user) =>
        access.Visible(Actor(user), registry.Connected, h => h.HostId))
    .RequireAuthorization("AdminOnly");

// Admin-assigned nicknames for PCs (hostId -> friendly name).
app.MapGet("/api/host-labels", (HostLabelStore labels, HostAccess access, System.Security.Claims.ClaimsPrincipal user) =>
        access.Visible(Actor(user), labels.GetAll(), kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value))
    .RequireAuthorization("AdminOnly");

app.MapPost("/api/hosts/{hostId}/label",
    (string hostId, HostLabelInput input, HostLabelStore labels, HostAccess access, AuditStore audit,
     System.Security.Claims.ClaimsPrincipal user) =>
    {
        if (!access.CanSee(Actor(user), hostId)) return Results.Forbid();
        labels.Set(hostId, input.Nickname);
        var what = string.IsNullOrWhiteSpace(input.Nickname) ? $"Cleared nickname for {hostId}" : $"Named {hostId} “{input.Nickname.Trim()}”";
        audit.Log(Actor(user), "host-label", hostId, what);
        return Results.NoContent();
    }).RequireAuthorization("AdminOnly");

app.MapGet("/api/hosts/{hostId}/activity",
        (string hostId, ActivityStore store, HostAccess access, System.Security.Claims.ClaimsPrincipal user,
         DateTimeOffset? from, DateTimeOffset? to, string? q, int? limit, int? offset) =>
            access.CanSee(Actor(user), hostId)
                ? Results.Ok(store.Query(hostId, from, to, q, limit ?? 200, offset ?? 0))
                : Results.Forbid())
    .RequireAuthorization("AdminOnly");

// ---- Rules (admin JWT required) ----

app.MapGet("/api/rules", (AlertStore store) => store.GetRules())
    .RequireAuthorization("AdminOnly");

app.MapPost("/api/rules", (RuleInput input, AlertStore store, RuleEngine engine, AuditStore audit,
        System.Security.Claims.ClaimsPrincipal user) =>
    {
        var rule = store.Create(input);
        engine.Reload();
        audit.Log(Actor(user), AuditActions.RuleCreate, null, $"Created rule '{rule.Name}' ({rule.Kind}:{rule.Pattern})");
        return Results.Ok(rule);
    }).RequireAuthorization("AdminOnly");

app.MapPut("/api/rules/{id:long}", (long id, RuleInput input, AlertStore store, RuleEngine engine, AuditStore audit,
        System.Security.Claims.ClaimsPrincipal user) =>
    {
        if (!store.Update(id, input)) return Results.NotFound();
        engine.Reload();
        audit.Log(Actor(user), AuditActions.RuleUpdate, null, $"Updated rule #{id} '{input.Name}' (enabled={input.Enabled})");
        return Results.NoContent();
    }).RequireAuthorization("AdminOnly");

app.MapDelete("/api/rules/{id:long}", (long id, AlertStore store, RuleEngine engine, AuditStore audit,
        System.Security.Claims.ClaimsPrincipal user) =>
    {
        if (!store.Delete(id)) return Results.NotFound();
        engine.Reload();
        audit.Log(Actor(user), AuditActions.RuleDelete, null, $"Deleted rule #{id}");
        return Results.NoContent();
    }).RequireAuthorization("AdminOnly");

// ---- Alerts (admin JWT required) ----

app.MapGet("/api/alerts",
        (AlertStore store, HostAccess access, System.Security.Claims.ClaimsPrincipal user,
         string? host, string? severity, bool? acknowledged, int? limit, int? offset) =>
        {
            var me = Actor(user);
            if (!string.IsNullOrWhiteSpace(host) && !access.CanSee(me, host)) return Results.Forbid();
            return Results.Ok(store.QueryAlerts(host, severity, acknowledged, limit ?? 200, offset ?? 0, access.DeniedFor(me)));
        })
    .RequireAuthorization("AdminOnly");

app.MapPost("/api/alerts/{id:long}/ack", (long id, AlertStore store, HostAccess access, AuditStore audit,
        System.Security.Claims.ClaimsPrincipal user) =>
    {
        if (!store.Acknowledge(id, access.DeniedFor(Actor(user)))) return Results.NotFound();
        audit.Log(Actor(user), AuditActions.AlertAck, null, $"Acknowledged alert #{id}");
        return Results.NoContent();
    }).RequireAuthorization("AdminOnly");

// Bulk: acknowledge every unacknowledged alert the caller can see.
app.MapPost("/api/alerts/ack-all", (AlertStore store, HostAccess access, AuditStore audit,
        System.Security.Claims.ClaimsPrincipal user) =>
    {
        var count = store.AcknowledgeAll(access.DeniedFor(Actor(user)));
        audit.Log(Actor(user), AuditActions.AlertAck, null, $"Acknowledged all alerts ({count})");
        return Results.Ok(new { acknowledged = count });
    }).RequireAuthorization("AdminOnly");

// ---- Admin audit trail (admin JWT required) ----

app.MapGet("/api/audit", (AuditStore audit, HostAccess access, System.Security.Claims.ClaimsPrincipal user,
        string? actor, string? action, int? limit, int? offset) =>
        audit.Query(actor, action, limit ?? 300, offset ?? 0, access.DeniedFor(Actor(user))))
    .RequireAuthorization("AdminOnly");

// ---- Reporting (admin JWT required) ----

app.MapGet("/api/reports/summary",
    (ActivityStore store, CategoryStore cats, HostAccess access, System.Security.Claims.ClaimsPrincipal user,
     string? host, DateTimeOffset? from, DateTimeOffset? to) =>
    {
        var me = Actor(user);
        if (!string.IsNullOrWhiteSpace(host) && !access.CanSee(me, host)) return Results.Forbid();
        var hidden = access.DeniedFor(me);

        var topApps = store.TopProcesses(host, from, to, hidden)
            .Select(b => new { name = b.Key, count = b.Count, classification = cats.Classify(b.Key, null) })
            .ToList();
        var topUrls = store.TopUrls(host, from, to, hidden)
            .Select(b => new { name = b.Key, count = b.Count, classification = cats.Classify("", b.Key) })
            .ToList();
        var daily = store.DailyCounts(host, from, to, hidden).Select(b => new { date = b.Key, count = b.Count });

        // Productivity split, estimated from the app mix (each focus-change = one sample).
        var byClass = new Dictionary<string, long> {
            [Classifications.Productive] = 0, [Classifications.Unproductive] = 0, [Classifications.Neutral] = 0 };
        foreach (var a in topApps) byClass[a.classification] += a.count;
        foreach (var u in topUrls) if (u.classification != Classifications.Neutral) byClass[u.classification] += u.count;

        return Results.Ok(new { totalEvents = store.CountEvents(host, from, to, hidden), topApps, topUrls, daily, byClassification = byClass });
    }).RequireAuthorization("AdminOnly");

app.MapGet("/api/reports/export",
    (ActivityStore store, HostAccess access, string? host, DateTimeOffset? from, DateTimeOffset? to,
     System.Security.Claims.ClaimsPrincipal user, AuditStore audit) =>
    {
        var me = Actor(user);
        if (!string.IsNullOrWhiteSpace(host) && !access.CanSee(me, host)) return Results.Forbid();

        audit.Log(me, AuditActions.Export, host, "Exported activity CSV");
        var rows = store.Query(host, from, to, null, 5000, 0, access.DeniedFor(me));
        var sb = new System.Text.StringBuilder("Timestamp,Host,Process,Window,URL,Kind\n");
        static string Q(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        foreach (var e in rows)
            sb.Append($"{e.TimestampUtc:o},{Q(e.HostId)},{Q(e.ProcessName)},{Q(e.WindowTitle)},{Q(e.Url)},{Q(e.ActivityKind)}\n");
        return Results.File(System.Text.Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", "managertool-activity.csv");
    }).RequireAuthorization("AdminOnly");

// ---- Categories (admin JWT required) ----

app.MapGet("/api/categories", (CategoryStore cats) => cats.GetAll()).RequireAuthorization("AdminOnly");
app.MapPost("/api/categories", (CategoryInput input, CategoryStore cats, AuditStore audit,
        System.Security.Claims.ClaimsPrincipal user) =>
    { var c = cats.Create(input); audit.Log(Actor(user), AuditActions.CategoryChange, null, $"Added category '{c.Name}'"); return Results.Ok(c); })
    .RequireAuthorization("AdminOnly");
app.MapDelete("/api/categories/{id:long}", (long id, CategoryStore cats, AuditStore audit,
        System.Security.Claims.ClaimsPrincipal user) =>
    { if (!cats.Delete(id)) return Results.NotFound(); audit.Log(Actor(user), AuditActions.CategoryChange, null, $"Deleted category #{id}"); return Results.NoContent(); })
    .RequireAuthorization("AdminOnly");

// ---- Recording playback (admin JWT required) ----

app.MapGet("/api/recordings", (RecordingLibrary lib, HostAccess access,
        System.Security.Claims.ClaimsPrincipal user, string? host) =>
    {
        var me = Actor(user);
        if (!string.IsNullOrWhiteSpace(host) && !access.CanSee(me, host)) return Results.Forbid();
        return Results.Ok(access.Visible(me, lib.ListSessions(host), s => s.HostId));
    }).RequireAuthorization("AdminOnly");
app.MapGet("/api/recordings/{host}/{session}/frames", (string host, string session, RecordingLibrary lib,
        HostAccess access, System.Security.Claims.ClaimsPrincipal user) =>
        access.CanSee(Actor(user), host) ? Results.Ok(lib.ListFrames(host, session)) : Results.Forbid())
    .RequireAuthorization("AdminOnly");
app.MapGet("/api/recordings/{host}/{session}/frame/{name}", (string host, string session, string name,
        RecordingLibrary lib, HostAccess access, System.Security.Claims.ClaimsPrincipal user) =>
    {
        if (!access.CanSee(Actor(user), host)) return Results.Forbid();
        var path = lib.FramePath(host, session, name);
        return path is null ? Results.NotFound() : Results.File(path, "image/jpeg");
    }).RequireAuthorization("AdminOnly");

// ---- Remote agent update: serve the latest installer + version (authenticated) ----

var installerPath = builder.Configuration["ManagerTool:AgentInstallerPath"]
                    ?? Path.Combine(AppContext.BaseDirectory, "agent-installer", "ManagerToolAgentSetup.exe");
var latestAgentVersion = builder.Configuration["ManagerTool:AgentVersion"] ?? "1.0.0";

app.MapGet("/api/agent/version", () => Results.Ok(new { version = latestAgentVersion, available = File.Exists(installerPath) }))
    .RequireAuthorization();

// The agent (or admin) downloads this with its own bearer token; it is never public because
// the installer embeds the enrollment key.
app.MapGet("/api/agent/installer", () =>
        File.Exists(installerPath)
            ? Results.File(installerPath, "application/octet-stream", "ManagerToolAgentSetup.exe")
            : Results.NotFound("No installer uploaded on the server."))
    .RequireAuthorization();

// ---- Canary-staged auto-update: the agent asks whether it is its turn ----
// The agent polls this on connect and periodically. Answer is "yes" only when the host is behind
// the target AND (the fleet is open OR this host is the canary). No explicit policy = update-all.
app.MapGet("/api/agent/rollout",
    (string? current, UpdateStore updates, IConfiguration cfg, System.Security.Claims.ClaimsPrincipal user) =>
    {
        var hostId = Actor(user);
        var policy = updates.GetRollout();
        var target = policy?.TargetVersion ?? cfg["ManagerTool:AgentVersion"] ?? "";
        var canary = policy?.CanaryHostId;
        var fleetOpen = policy?.FleetOpen ?? true;   // no policy configured => everyone eligible

        var behind = target.Length > 0 && UpdateStore.CompareVersions(current, target) < 0;
        var myTurn = fleetOpen || hostId == canary;
        var should = behind && myTurn;
        var reason = !behind ? "up to date"
                    : !myTurn ? "held — waiting for canary to prove healthy"
                    : (hostId == canary && !fleetOpen) ? "canary — updating first" : "update available";

        // Rollback-loop guard: never keep telling a host to re-download a build it just failed on.
        // A failed/rolledback attempt holds this host for a cooldown; an admin re-saving the rollout
        // (which advances the policy's UpdatedUtc past the failure) lifts the hold immediately for a
        // deliberate retry. An in-progress attempt also holds briefly so we don't overlap it.
        if (should)
        {
            var last = updates.GetStatus(hostId);
            if (last is not null)
            {
                var age = DateTimeOffset.UtcNow - last.Utc;
                var policyTime = policy?.UpdatedUtc ?? DateTimeOffset.MinValue;
                var inProgress = last.Stage is "triggered" or "downloading" or "installing" or "verifying";
                var failedRecently = last.Stage is "failed" or "rolledback";

                if (inProgress && age < TimeSpan.FromMinutes(15))
                {
                    should = false; reason = "update already in progress";
                }
                else if (failedRecently && age < TimeSpan.FromHours(6) && last.Utc >= policyTime)
                {
                    should = false;
                    reason = $"previous update to {target} failed ({last.Stage}); holding — re-save the rollout to retry now";
                }
            }
        }
        return Results.Ok(new RolloutDecision(should, target, reason));
    }).RequireAuthorization("AgentOnly");

// The SYSTEM updater (update.ps1) reports each stage here; it outlives the agent process, so this is
// the reliable spine of update observability. Host identity comes from the caller's token, not body.
app.MapPost("/api/agent/update-status",
    async (UpdateStatus status, UpdateCoordinator updates, System.Security.Claims.ClaimsPrincipal user) =>
    {
        await updates.ApplyAsync(status with { HostId = Actor(user), Utc = DateTimeOffset.UtcNow });
        return Results.Ok();
    }).RequireAuthorization("AgentOnly");

// ---- Admin: read + drive the rollout, and read every host's last update stage ----
app.MapGet("/api/rollout",
    (UpdateStore updates, IConfiguration cfg) =>
        Results.Ok(updates.GetRollout()
                   ?? new RolloutPolicy(cfg["ManagerTool:AgentVersion"] ?? "", null, true)))
    .RequireAuthorization("AdminOnly");

app.MapPost("/api/rollout",
    (RolloutRequest req, UpdateStore updates, AuditStore audit, System.Security.Claims.ClaimsPrincipal user) =>
    {
        if (string.IsNullOrWhiteSpace(req.TargetVersion))
            return Results.BadRequest("A target version is required.");
        updates.SetRollout(req.TargetVersion.Trim(),
                           string.IsNullOrWhiteSpace(req.CanaryHostId) ? null : req.CanaryHostId);
        audit.Log(Actor(user), AuditActions.RolloutSet, req.CanaryHostId,
                  $"Rollout target {req.TargetVersion}" +
                  (string.IsNullOrWhiteSpace(req.CanaryHostId) ? " (all hosts)" : $" via canary {req.CanaryHostId}"));
        return Results.Ok(updates.GetRollout());
    }).RequireAuthorization("AdminOnly");

app.MapGet("/api/hosts/update-status",
    (UpdateStore updates, HostAccess access, System.Security.Claims.ClaimsPrincipal user) =>
    {
        var actor = Actor(user);
        return Results.Ok(updates.AllStatus().Where(s => access.CanSee(actor, s.HostId)));
    }).RequireAuthorization("AdminOnly");

// ---- One-click PUBLIC agent download (no credentials) ----
// Visiting the link downloads the installer directly on every browser: Results.File with a
// download name sets Content-Disposition: attachment, and octet-stream stops any browser trying
// to render it. HEAD is handled too so link-preview bots don't 405.
// NOTE: this deliberately exposes the installer — which embeds the enrollment key — to anyone with
// the link. That trade-off was accepted for this deployment; keep the URL to trusted recipients.
//
// Cache-Control: no-store is load-bearing, not cosmetic. This URL never changes even though the
// FILE behind it does on every release — without an explicit no-store, a browser (or a corporate
// proxy) that already has an older response cached for this exact path will silently keep serving
// it forever, with no error and no way for the person clicking the link to notice. That cost a
// full day chasing a "why does the dashboard show the old version" ghost that was actually a
// stale cached download of an old installer, never re-fetched from the server at all.
static IResult ServeInstaller(string installerPath, HttpContext ctx)
{
    ctx.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate, max-age=0";
    ctx.Response.Headers.Pragma = "no-cache";
    return File.Exists(installerPath)
        ? Results.File(installerPath, "application/octet-stream", "ManagerToolAgentSetup.exe")
        : Results.NotFound("No installer is available yet.");
}

// Short aliases (/d, /get) sit alongside the original routes so old links keep working.
// /dl is taken by the Basic-Auth route below, so the shortest free path is /d.
foreach (var route in new[] { "/d", "/get", "/download", "/agent", "/ManagerToolAgentSetup.exe" })
    app.MapMethods(route, new[] { "GET", "HEAD" }, (HttpContext ctx) => ServeInstaller(installerPath, ctx)).AllowAnonymous();

// ---- Public agent download (HTTP Basic Auth, password managed by the owner) ----
// nginx proxies example.com/agent/<file> here. Username is fixed "managertool"; the password hash
// lives in the settings store and is set from the dashboard. Falls back to the config seed.
const string DownloadUser = "managertool";
app.MapMethods("/dl/{file}", new[] { "GET", "HEAD" }, (string file, HttpContext ctx, SettingsStore settings, IConfiguration cfg) =>
{
    IResult Challenge()
    {
        ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"Manager Tool Agent Download\"";
        return Results.StatusCode(401);
    }

    var header = ctx.Request.Headers.Authorization.ToString();
    if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        return Challenge();

    string user, pass;
    try
    {
        var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
        var i = decoded.IndexOf(':');
        if (i < 0) return Challenge();
        user = decoded[..i]; pass = decoded[(i + 1)..];
    }
    catch { return Challenge(); }

    var storedHash = settings.Get(SettingsStore.DownloadPasswordHash) ?? cfg["ManagerTool:DownloadPasswordHash"];
    var ok = storedHash is not null
             && string.Equals(user, DownloadUser, StringComparison.OrdinalIgnoreCase)
             && PasswordHasher.Verify(pass, storedHash);
    if (!ok)
        return Challenge();

    var name = Path.GetFileName(file);
    var path = Path.Combine(Path.GetDirectoryName(installerPath)!, name);
    ctx.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate, max-age=0";
    ctx.Response.Headers.Pragma = "no-cache";
    return File.Exists(path)
        ? Results.File(path, "application/octet-stream", name)
        : Results.NotFound();
});

// Owner sets the agent-download password.
app.MapPost("/api/settings/download-password",
    (ChangePasswordRequest req, SettingsStore settings, TokenService tokens, AuditStore audit,
     System.Security.Claims.ClaimsPrincipal user) =>
    {
        if (!tokens.IsOwner(Actor(user))) return Results.Forbid();
        if (req.NewPassword is null || req.NewPassword.Length < 8)
            return Results.BadRequest("Download password must be at least 8 characters.");
        settings.Set(SettingsStore.DownloadPasswordHash, PasswordHasher.Hash(req.NewPassword));
        audit.Log(Actor(user), "download-password-set", null, "Changed the agent-download password");
        return Results.NoContent();
    }).RequireAuthorization("AdminOnly");

// Owner resets another admin's password.
app.MapPost("/api/admins/{username}/reset-password",
    (string username, ChangePasswordRequest req, TokenService tokens, PasswordResetStore resets,
     AuditStore audit, System.Security.Claims.ClaimsPrincipal user) =>
    {
        if (!tokens.IsOwner(Actor(user))) return Results.Forbid();
        if (req.NewPassword is null || req.NewPassword.Length < 8)
            return Results.BadRequest("New password must be at least 8 characters.");
        if (!tokens.ResetAdminPassword(username, req.NewPassword)) return Results.NotFound();

        // Actioning the lockout also clears it from the queue, so the owner isn't asked twice.
        resets.Clear(username);
        audit.Log(Actor(user), "admin-reset-password", null, $"Reset password for admin '{username}'");
        return Results.NoContent();
    }).RequireAuthorization("AdminOnly");

app.MapGet("/health", () => Results.Text("Manager Tool server running (auth + TLS enabled).", "text/plain"));

// Extracts the admin username (JWT subject) from the request principal for audit records.
static string Actor(System.Security.Claims.ClaimsPrincipal user) =>
    user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
    ?? user.Identity?.Name ?? "admin";

app.Run();
