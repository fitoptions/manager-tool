using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using ManagerTool.Agent.Capture;
using ManagerTool.Shared;

namespace ManagerTool.Agent;

/// <summary>
/// Owns the authenticated SignalR connection and glues capture components to it:
///   - always-on: active-window/URL activity stream
///   - on-demand: screen streaming, started only when the server says an admin is viewing
///
/// The agent first enrolls with the pre-shared enrollment key to obtain a JWT, then
/// connects over TLS presenting that token.
/// </summary>
public sealed class AgentConnection : IAsyncDisposable
{
    private readonly AgentConfig _config;
    private readonly HostInfo _host;
    private readonly ActiveWindowWatcher _activity;
    private readonly ScreenCapturer _screen;
    private readonly ActivityBuffer _buffer;
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    /// The running agent version. Bumped on every shipped change so the server's "latest" check and
    /// the self-update health gate can tell builds apart. Also carried in the User-Agent header.
    public const string AgentVersion = "1.2.5";

    /// Cloudflare blocks requests that arrive with no User-Agent header, and .NET's
    /// HttpClient sends none by default. Every HTTP path the agent uses -- enrollment
    /// and the SignalR negotiate/WebSocket -- sets this explicitly.
    private const string UserAgentValue = "ManagerToolAgent/" + AgentVersion + " (+windows)";

    private readonly HealthBeacon _beacon;

    private HubConnection? _connection;
    private TokenResponse? _token;

    public AgentConnection(AgentConfig config, HostInfo host)
    {
        _config = config;
        _host = host;
        _beacon = new HealthBeacon(AgentVersion, host.HostId);
        _buffer = new ActivityBuffer();

        _activity = new ActiveWindowWatcher(host.HostId);
        _activity.ActivityChanged += OnActivity;

        _screen = new ScreenCapturer(host.HostId);
        _screen.TilesCaptured += OnTiles;
    }

    public async Task StartAsync()
    {
        await EnsureTokenAsync();
        BuildConnection();
        await ConnectWithRetry();
        await Register();
        _beacon.Beat();          // connected — a fresh install can now be judged healthy
        _activity.Start();
        _ = FlushLoopAsync();    // periodically drain any buffered backlog
        _ = BeatLoopAsync();     // keep the heartbeat fresh so a health check has a recent stamp
        // NOTE: auto-update is now owned by the watchdog service (ManagerToolSvc / AgentUpdater),
        // which polls the server and performs the update as SYSTEM. The agent only needs to keep its
        // heartbeat fresh so the watchdog can verify a new build connected. This removed the fragile
        // agent -> hub -> scheduled-task -> update.ps1 chain.
    }

    /// <summary>Refresh the connected-heartbeat periodically while the hub is up.</summary>
    private async Task BeatLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(20), _cts.Token); }
            catch (OperationCanceledException) { break; }
            if (_connection?.State == HubConnectionState.Connected)
                _beacon.Beat();
        }
    }

    /// <summary>Creates an HttpClientHandler that optionally trusts a dev/self-signed cert.</summary>
    private HttpClientHandler CreateHandler()
    {
        var handler = new HttpClientHandler();
        if (_config.AllowInvalidServerCert)
        {
            // DEV ONLY. In production leave AllowInvalidServerCert=false so the agent
            // verifies the server certificate against the trust store.
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }
        return handler;
    }

    private async Task EnsureTokenAsync()
    {
        using var http = new HttpClient(CreateHandler()) { BaseAddress = new Uri(_config.ServerUrl) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentValue);
        var req = new AgentEnrollRequest(_config.EnrollmentKey, _host.HostId, _host.MachineName);

        while (true)
        {
            try
            {
                var resp = await http.PostAsJsonAsync("/api/auth/enroll", req);
                if (resp.IsSuccessStatusCode)
                {
                    _token = await resp.Content.ReadFromJsonAsync<TokenResponse>();
                    if (_token is not null)
                        return;
                }
                // 401 = bad enrollment key. Keep retrying slowly; a wrong key is an ops fix.
            }
            catch
            {
                // server unreachable
            }
            await Task.Delay(TimeSpan.FromSeconds(10));
        }
    }

    private void BuildConnection()
    {
        _connection = new HubConnectionBuilder()
            .WithUrl($"{_config.ServerUrl.TrimEnd('/')}{HubRoutes.Path}", options =>
            {
                options.Headers["User-Agent"] = UserAgentValue;

                options.AccessTokenProvider = async () =>
                {
                    if (_token is null || _token.ExpiresAtUtc <= DateTimeOffset.UtcNow.AddMinutes(1))
                        await EnsureTokenAsync();
                    return _token?.AccessToken;
                };

                if (_config.AllowInvalidServerCert)
                {
                    options.HttpMessageHandlerFactory = _ => CreateHandler();
                    options.WebSocketConfiguration = ws =>
                        ws.RemoteCertificateValidationCallback = (_, _, _, _) => true;
                }
            })
            .WithAutomaticReconnect(new PerpetualRetryPolicy())
            .Build();

        _connection.On(nameof(IAgentClient.BeginScreenStream), () => { _screen.SetMonitor(0); _screen.Start(); });
        _connection.On(nameof(IAgentClient.EndScreenStream), () => _screen.Stop());
        _connection.On<int>(nameof(IAgentClient.SetMonitor), i => _screen.SetMonitor(i));
        _connection.On(nameof(IAgentClient.RequestKeyframe), () => _screen.RequestKeyframe());
        _connection.On(nameof(IAgentClient.Uninstall), () => Uninstaller.Run());
        _connection.On(nameof(IAgentClient.Update), () => Updater.Run());
        _connection.Reconnected += async _ =>
        {
            await Register();
            _beacon.Beat();       // reconnected — refresh health so an update in flight sees it live
            await FlushAsync();   // drain whatever accumulated while disconnected
        };

        // Backstop: if automatic reconnect ever gives up (or the connection closes for any
        // other reason), climb back on our own — perpetually — unless we are shutting down.
        // This is what guarantees the agent re-attaches to the server after ANY failure,
        // including outages longer than the automatic-reconnect window.
        _connection.Closed += async _ =>
        {
            if (_cts.IsCancellationRequested) return;
            await Task.Delay(TimeSpan.FromSeconds(5));
            await ConnectWithRetry();   // loops forever until connected
            try { await Register(); await FlushAsync(); } catch { /* next tick retries */ }
        };
    }

    private async Task ConnectWithRetry()
    {
        while (true)
        {
            try
            {
                await _connection!.StartAsync();
                return;
            }
            catch
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }
    }

    private Task Register() => _connection!.InvokeAsync(nameof(IMonitorHub.RegisterHost), _host);

    private async void OnActivity(ActivityEvent evt)
    {
        // Durable-first: persist every event, then try to drain. Nothing is lost if the
        // server is unreachable — it replays in order once the connection is back.
        _buffer.Append(evt);
        await FlushAsync();
    }

    /// <summary>Periodic safety-net drain, in case a flush trigger was missed.</summary>
    private async Task FlushLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(15), _cts.Token); }
            catch (OperationCanceledException) { break; }
            await FlushAsync();
        }
    }

    /// <summary>
    /// Sends buffered events oldest-first while connected. Stops at the first failure to
    /// preserve order, and removes only what was acknowledged. Single-flight via a gate so
    /// concurrent triggers (activity + timer + reconnect) don't double-send.
    /// </summary>
    private async Task FlushAsync()
    {
        if (_connection is null || _connection.State != HubConnectionState.Connected)
            return;
        if (!await _flushGate.WaitAsync(0))
            return;   // a flush is already in progress

        try
        {
            var pending = _buffer.Snapshot();
            var sent = 0;
            foreach (var evt in pending)
            {
                try
                {
                    await _connection.InvokeAsync(nameof(IMonitorHub.PushActivity), evt);
                    sent++;
                }
                catch
                {
                    break;   // connection dropped mid-drain; keep the rest for next time
                }
            }
            if (sent > 0)
                _buffer.RemoveFirst(sent);
        }
        finally
        {
            _flushGate.Release();
        }
    }

    // Guards against piling frames onto a slow uplink: if the previous PushFrame has not
    // completed, this frame is dropped rather than queued, so we always send the freshest
    // one and latency stays bounded (0 = idle, 1 = a send is in flight).
    private int _sending;

    private async void OnTiles(ScreenTileFrame frame)
    {
        // Single-flight: never queue behind a push still on the wire — a backlog is what turns a
        // slow link into ever-growing latency. But the capturer has already advanced its per-tile
        // hashes for this frame's tiles, so a frame that is skipped or fails would leave those tiles
        // stale on the viewer until the 5s safety keyframe. Force a keyframe next tick instead, so
        // any dropped update is made good within ~125ms rather than seconds.
        if (Interlocked.CompareExchange(ref _sending, 1, 0) != 0)
        {
            _screen.RequestKeyframe();
            return;
        }

        try
        {
            await _connection!.InvokeAsync(nameof(IMonitorHub.PushTiles), frame);
        }
        catch
        {
            _screen.RequestKeyframe();   // transient send failure — resync fully next tick
        }
        finally
        {
            Interlocked.Exchange(ref _sending, 0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _activity.Dispose();
        _screen.Dispose();
        if (_connection is not null)
            await _connection.DisposeAsync();
        _cts.Dispose();
        _flushGate.Dispose();
    }
}

/// <summary>
/// SignalR reconnect policy that never gives up: retries quickly at first, then settles to a
/// steady cadence. Returning a non-null delay forever is what keeps the agent trying no matter
/// how long the server is unreachable (the default policy stops after ~42 seconds).
/// </summary>
internal sealed class PerpetualRetryPolicy : IRetryPolicy
{
    public TimeSpan? NextRetryDelay(RetryContext ctx) => ctx.PreviousRetryCount switch
    {
        0 => TimeSpan.FromSeconds(0),
        1 => TimeSpan.FromSeconds(2),
        2 => TimeSpan.FromSeconds(5),
        3 => TimeSpan.FromSeconds(10),
        _ => TimeSpan.FromSeconds(15),   // steady 15s forever
    };
}
