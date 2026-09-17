using System.Windows.Threading;
using ManagerTool.Agent.Native;
using ManagerTool.Shared;

namespace ManagerTool.Agent.Capture;

/// <summary>
/// Polls the foreground window on an interval and raises an ActivityEvent whenever the
/// focused app/window/URL changes. Emitting on change (not every tick) keeps the stream
/// compact while still capturing "what were they looking at, and when".
/// </summary>
public sealed class ActiveWindowWatcher : IDisposable
{
    private readonly string _hostId;
    private readonly DispatcherTimer _timer;
    private string _lastSignature = string.Empty;

    public event Action<ActivityEvent>? ActivityChanged;

    public ActiveWindowWatcher(string hostId, TimeSpan? interval = null)
    {
        _hostId = hostId;
        _timer = new DispatcherTimer { Interval = interval ?? TimeSpan.FromMilliseconds(1500) };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    private void Poll()
    {
        var fg = Win32.GetForeground();
        if (fg is null)
            return;

        var info = fg.Value;

        string? url = null;
        if (Win32.IsBrowser(info.ProcessName))
            url = BrowserUrlReader.TryReadUrl(info.Handle);

        // Signature = what we consider a "distinct" focus state. A new URL within the
        // same browser window counts as a change.
        var signature = $"{info.ProcessName}|{info.WindowTitle}|{url}";
        if (signature == _lastSignature)
            return;
        _lastSignature = signature;

        var kind = url is not null ? "url" : "window-focus";
        var evt = new ActivityEvent(
            _hostId,
            DateTimeOffset.UtcNow,
            info.ProcessName,
            info.WindowTitle,
            url,
            kind);

        ActivityChanged?.Invoke(evt);
    }

    public void Dispose() => _timer.Stop();
}
