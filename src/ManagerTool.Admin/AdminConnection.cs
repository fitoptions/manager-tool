using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using ManagerTool.Shared;

namespace ManagerTool.Admin;

/// <summary>
/// Admin-side SignalR client. Authenticates via /api/auth/login to obtain an admin JWT,
/// then connects over TLS presenting that token. Receives host connect/disconnect, the
/// live activity stream, and screen frames for the host currently being viewed.
/// </summary>
public sealed class AdminConnection : IAsyncDisposable
{
    private readonly string _serverUrl;
    private readonly bool _allowInvalidCert;
    private HubConnection? _connection;
    private TokenResponse? _token;

    public event Action<HostInfo>? HostConnected;
    public event Action<string>? HostDisconnected;
    public event Action<ActivityEvent>? ActivityReceived;
    public event Action<ScreenFrame>? FrameReceived;
    public event Action<ScreenTileFrame>? TilesReceived;
    public event Action<Alert>? AlertReceived;

    /// <summary>Who is watching a host right now (hostId, usernames) — several admins may share a feed.</summary>
    public event Action<string, string[]>? ViewersChanged;

    /// <summary>This account's workstation access changed; the payload is the set now blocked.</summary>
    public event Action<string[]>? AccessChanged;

    /// <summary>The signed-in username, taken from the JWT subject claim.</summary>
    public string Username { get; private set; } = "";

    public AdminConnection(string serverUrl, bool allowInvalidCert)
    {
        _serverUrl = serverUrl;
        _allowInvalidCert = allowInvalidCert;
    }

    private HttpClientHandler CreateHandler()
    {
        var handler = new HttpClientHandler();
        if (_allowInvalidCert)
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator; // DEV ONLY
        return handler;
    }

    /// <summary>Returns true on successful credential exchange.</summary>
    public async Task<bool> LoginAsync(string username, string password)
    {
        using var http = new HttpClient(CreateHandler()) { BaseAddress = new Uri(_serverUrl) };
        var resp = await http.PostAsJsonAsync("/api/auth/login", new LoginRequest(username, password));
        if (!resp.IsSuccessStatusCode)
            return false;

        _token = await resp.Content.ReadFromJsonAsync<TokenResponse>();
        if (_token is null)
            return false;

        Username = username;
        return true;
    }

    public async Task StartAsync()
    {
        if (_token is null)
            throw new InvalidOperationException("Call LoginAsync successfully before StartAsync.");

        _connection = new HubConnectionBuilder()
            .WithUrl($"{_serverUrl.TrimEnd('/')}{HubRoutes.Path}", options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(_token!.AccessToken);
                if (_allowInvalidCert)
                {
                    options.HttpMessageHandlerFactory = _ => CreateHandler();
                    options.WebSocketConfiguration = ws =>
                        ws.RemoteCertificateValidationCallback = (_, _, _, _) => true;
                }
            })
            .WithAutomaticReconnect()
            .Build();

        _connection.On<HostInfo>(nameof(IAdminClient.HostConnected), h => HostConnected?.Invoke(h));
        _connection.On<string>(nameof(IAdminClient.HostDisconnected), id => HostDisconnected?.Invoke(id));
        _connection.On<ActivityEvent>(nameof(IAdminClient.ActivityReceived), e => ActivityReceived?.Invoke(e));
        _connection.On<ScreenFrame>(nameof(IAdminClient.FrameReceived), f => FrameReceived?.Invoke(f));
        _connection.On<ScreenTileFrame>(nameof(IAdminClient.TilesReceived), f => TilesReceived?.Invoke(f));
        _connection.On<Alert>(nameof(IAdminClient.AlertReceived), a => AlertReceived?.Invoke(a));
        _connection.On<string, string[]>(nameof(IAdminClient.ViewersChanged), (h, v) => ViewersChanged?.Invoke(h, v));
        _connection.On<string[]>(nameof(IAdminClient.AccessChanged), d => AccessChanged?.Invoke(d));

        // Live frames go to a per-host group joined only inside StartViewing. Automatic reconnect
        // returns with a NEW connection id, dropping that membership — the feed then freezes on its
        // last frame. Re-issue StartViewing for whatever is on screen so it heals itself rather than
        // forcing a manual Stop/View. The set is tiny (usually one host) and re-subscribing is safe.
        _connection.Reconnected += async _ =>
        {
            foreach (var hostId in _viewing.ToArray())
            {
                try { await _connection.InvokeAsync(nameof(IMonitorHub.StartViewing), hostId); }
                catch { /* next reconnect or a manual View retries */ }
            }
        };

        await _connection.StartAsync();
    }

    /// <summary>
    /// Fetches the hosts already connected to the server. The live HostConnected push only
    /// fires for hosts that connect AFTER this admin subscribes, so this REST snapshot is
    /// needed to show hosts that were already online when the console opened.
    /// </summary>
    public async Task<IReadOnlyList<HostInfo>> GetHostsAsync()
    {
        if (_token is null)
            return Array.Empty<HostInfo>();

        using var http = new HttpClient(CreateHandler()) { BaseAddress = new Uri(_serverUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token.AccessToken);
        var hosts = await http.GetFromJsonAsync<List<HostInfo>>("/api/hosts");
        return hosts ?? new List<HostInfo>();
    }

    /// <summary>
    /// Activity history for a host (newest-first), from the query API. All filters optional:
    /// time window, free-text search (matches app / window title / URL), and pagination.
    /// </summary>
    public async Task<IReadOnlyList<ActivityEvent>> GetActivityAsync(
        string hostId,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? search = null,
        int limit = 200,
        int offset = 0)
    {
        if (_token is null)
            return Array.Empty<ActivityEvent>();

        var query = new List<string> { $"limit={limit}", $"offset={offset}" };
        if (from is not null) query.Add($"from={Uri.EscapeDataString(from.Value.ToString("o"))}");
        if (to is not null) query.Add($"to={Uri.EscapeDataString(to.Value.ToString("o"))}");
        if (!string.IsNullOrWhiteSpace(search)) query.Add($"q={Uri.EscapeDataString(search.Trim())}");

        using var http = new HttpClient(CreateHandler()) { BaseAddress = new Uri(_serverUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token.AccessToken);
        var events = await http.GetFromJsonAsync<List<ActivityEvent>>(
            $"/api/hosts/{hostId}/activity?{string.Join("&", query)}");
        return events ?? new List<ActivityEvent>();
    }

    /// <summary>Historical alerts, newest-first, with optional filters.</summary>
    public async Task<IReadOnlyList<Alert>> GetAlertsAsync(
        string? severity = null, bool? acknowledged = null, int limit = 200, int offset = 0)
    {
        if (_token is null) return Array.Empty<Alert>();
        var query = new List<string> { $"limit={limit}", $"offset={offset}" };
        if (!string.IsNullOrWhiteSpace(severity)) query.Add($"severity={severity}");
        if (acknowledged is not null) query.Add($"acknowledged={acknowledged.Value.ToString().ToLowerInvariant()}");

        using var http = new HttpClient(CreateHandler()) { BaseAddress = new Uri(_serverUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token.AccessToken);
        var alerts = await http.GetFromJsonAsync<List<Alert>>($"/api/alerts?{string.Join("&", query)}");
        return alerts ?? new List<Alert>();
    }

    /// <summary>Mark an alert acknowledged. Returns true on success.</summary>
    public async Task<bool> AcknowledgeAlertAsync(long id)
    {
        if (_token is null) return false;
        using var http = new HttpClient(CreateHandler()) { BaseAddress = new Uri(_serverUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token.AccessToken);
        var resp = await http.PostAsync($"/api/alerts/{id}/ack", null);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>
    /// The workstation-access matrix. Returns null when the signed-in account is not an owner —
    /// the server refuses the route, which is also how the console decides to hide the editor.
    /// </summary>
    public async Task<AccessMatrix?> GetAccessAsync()
    {
        if (_token is null) return null;
        using var http = new HttpClient(CreateHandler()) { BaseAddress = new Uri(_serverUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token.AccessToken);
        var resp = await http.GetAsync("/api/access");
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<AccessMatrix>() : null;
    }

    /// <summary>
    /// Reports a forgotten password so the owner can reset it. Static and anonymous by nature —
    /// the caller cannot sign in, which is the whole point. Says nothing about whether the account
    /// exists, because the server deliberately does not tell us.
    /// </summary>
    /// <returns>
    /// Whether a one-time code was sent (so the caller shows the code box) — or null if the server
    /// could not be reached. Says nothing about whether the account exists.
    /// </returns>
    public static async Task<bool?> RequestPasswordResetAsync(string serverUrl, string username, bool allowInvalidCert)
    {
        try
        {
            using var http = AnonymousClient(serverUrl, allowInvalidCert);
            var resp = await http.PostAsJsonAsync("/api/auth/reset-request", new PasswordResetRequest(username));
            if (!resp.IsSuccessStatusCode)
                return null;
            var result = await resp.Content.ReadFromJsonAsync<ForgotPasswordResult>();
            return result?.CodeSent ?? false;
        }
        catch
        {
            return null;   // unreachable server / bad URL
        }
    }

    /// <summary>
    /// Completes an emailed-code reset. Anonymous by nature — the caller cannot sign in yet.
    /// </summary>
    /// <returns>null on success, otherwise the server's (deliberately generic) reason.</returns>
    public static async Task<string?> CompletePasswordResetAsync(
        string serverUrl, string username, string code, string newPassword, bool allowInvalidCert)
    {
        try
        {
            using var http = AnonymousClient(serverUrl, allowInvalidCert);
            var resp = await http.PostAsJsonAsync("/api/auth/reset-password",
                new OtpResetRequest(username, code, newPassword));
            if (resp.IsSuccessStatusCode)
                return null;
            if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                return "Too many attempts just now — try again in a minute.";
            return Unwrap(await resp.Content.ReadAsStringAsync()) ?? "That code is invalid or has expired.";
        }
        catch
        {
            return "Could not reach the server.";
        }
    }

    private static HttpClient AnonymousClient(string serverUrl, bool allowInvalidCert)
    {
        var handler = new HttpClientHandler();
        if (allowInvalidCert)
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        return new HttpClient(handler) { BaseAddress = new Uri(serverUrl) };
    }

    /// <summary>Owner action: delete a viewer account, along with its access rules and any ticket.</summary>
    public async Task<bool> DeleteAdminAsync(string username)
    {
        if (_token is null) return false;
        using var http = new HttpClient(CreateHandler()) { BaseAddress = new Uri(_serverUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token.AccessToken);
        var resp = await http.DeleteAsync($"/api/admins/{Uri.EscapeDataString(username)}");
        return resp.IsSuccessStatusCode;
    }

    /// <summary>The owner's queue of accounts reporting a lockout.</summary>
    public async Task<IReadOnlyList<PasswordResetTicket>> GetResetRequestsAsync()
    {
        if (_token is null) return Array.Empty<PasswordResetTicket>();
        using var http = new HttpClient(CreateHandler()) { BaseAddress = new Uri(_serverUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token.AccessToken);
        var resp = await http.GetAsync("/api/admins/reset-requests");
        if (!resp.IsSuccessStatusCode) return Array.Empty<PasswordResetTicket>();
        return await resp.Content.ReadFromJsonAsync<List<PasswordResetTicket>>()
               ?? (IReadOnlyList<PasswordResetTicket>)Array.Empty<PasswordResetTicket>();
    }

    /// <summary>Owner action: force a new password on a viewer. Also clears their lockout ticket.</summary>
    public async Task<string?> ResetViewerPasswordAsync(string username, string newPassword)
    {
        if (_token is null) return "Not signed in.";
        using var http = new HttpClient(CreateHandler()) { BaseAddress = new Uri(_serverUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token.AccessToken);
        var resp = await http.PostAsJsonAsync($"/api/admins/{Uri.EscapeDataString(username)}/reset-password",
            new ChangePasswordRequest("", newPassword));
        return resp.IsSuccessStatusCode
            ? null
            : Unwrap(await resp.Content.ReadAsStringAsync()) ?? "Could not reset that password.";
    }

    /// <summary>Owner action: drop a lockout report without changing the password.</summary>
    public async Task<bool> DismissResetRequestAsync(string username)
    {
        if (_token is null) return false;
        using var http = new HttpClient(CreateHandler()) { BaseAddress = new Uri(_serverUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token.AccessToken);
        var resp = await http.DeleteAsync(
            $"/api/admins/reset-requests/{Uri.EscapeDataString(username)}");
        return resp.IsSuccessStatusCode;
    }

    /// <summary>
    /// Changes the signed-in account's own password. Available to every role — a viewer does not
    /// need the owner to rotate their credentials.
    /// </summary>
    /// <returns>null on success, otherwise the server's reason for refusing.</returns>
    public async Task<string?> ChangePasswordAsync(string currentPassword, string newPassword)
    {
        if (_token is null) return "Not signed in.";
        using var http = new HttpClient(CreateHandler()) { BaseAddress = new Uri(_serverUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token.AccessToken);
        var resp = await http.PostAsJsonAsync("/api/auth/change-password",
            new ChangePasswordRequest(currentPassword, newPassword));
        if (resp.IsSuccessStatusCode) return null;

        return Unwrap(await resp.Content.ReadAsStringAsync()) ?? "Could not change the password.";
    }

    /// <summary>
    /// Owner action: issue a viewer sign-in, active immediately.
    /// </summary>
    /// <returns>null on success, otherwise the server's reason for refusing.</returns>
    public async Task<string?> CreateAdminAsync(string username, string password)
    {
        if (_token is null) return "Not signed in.";
        using var http = new HttpClient(CreateHandler()) { BaseAddress = new Uri(_serverUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token.AccessToken);
        var resp = await http.PostAsJsonAsync("/api/admins", new CreateAdminRequest(username, password));
        if (resp.IsSuccessStatusCode) return null;

        var reason = await resp.Content.ReadAsStringAsync();
        return Unwrap(reason) ?? "Could not create that viewer.";
    }

    /// <summary>
    /// ASP.NET returns refusal strings as JSON ("..."), which would surface the quotes verbatim in
    /// the UI. Unwraps a JSON string body; falls back to the raw text.
    /// </summary>
    private static string? Unwrap(string body)
    {
        body = body.Trim();
        if (body.Length == 0) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.String)
                return doc.RootElement.GetString();
            if (doc.RootElement.TryGetProperty("title", out var title))
                return title.GetString();
        }
        catch (System.Text.Json.JsonException) { /* not JSON — show it as-is */ }
        return body;
    }

    /// <summary>Owner action: replace one viewer's blocked-workstation list. Empty = sees everything.</summary>
    public async Task<bool> SetAccessAsync(string username, IEnumerable<string> deniedHostIds)
    {
        if (_token is null) return false;
        using var http = new HttpClient(CreateHandler()) { BaseAddress = new Uri(_serverUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token.AccessToken);
        var resp = await http.PutAsJsonAsync(
            $"/api/access/{Uri.EscapeDataString(username)}", new HostAccessInput(deniedHostIds.ToArray()));
        return resp.IsSuccessStatusCode;
    }

    // What this console is currently watching, so it can be re-asserted after a reconnect.
    private readonly HashSet<string> _viewing = new();

    public Task StartViewing(string hostId)
    {
        _viewing.Add(hostId);
        return _connection!.InvokeAsync(nameof(IMonitorHub.StartViewing), hostId);
    }

    public Task StopViewing(string hostId)
    {
        _viewing.Remove(hostId);
        return _connection!.InvokeAsync(nameof(IMonitorHub.StopViewing), hostId);
    }

    public Task RemoteUninstall(string hostId) =>
        _connection!.InvokeAsync(nameof(IMonitorHub.RemoteUninstall), hostId);

    public Task StartRecording(string hostId) =>
        _connection!.InvokeAsync(nameof(IMonitorHub.StartRecording), hostId);

    public Task StopRecording(string hostId) =>
        _connection!.InvokeAsync(nameof(IMonitorHub.StopRecording), hostId);

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}
