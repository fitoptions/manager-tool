using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ManagerTool.Shared;

namespace ManagerTool.Admin;

public partial class MainWindow : Window
{
    private readonly AdminConnection _conn;
    private readonly ObservableCollection<HostInfo> _hosts = new();
    private readonly ObservableCollection<ActivityRow> _activity = new();

    private string? _viewingHostId;
    private int _selectedMonitor;
    private int _knownMonitorCount;
    private bool _recording;

    // Full-screen viewer state (restored on exit).
    private bool _isFullScreen;
    private WindowStyle _prevWindowStyle;
    private ResizeMode _prevResizeMode;
    private WindowState _prevWindowState;

    // Receives an already-authenticated, already-connected session from LoginWindow.
    public MainWindow(AdminConnection connection)
    {
        InitializeComponent();
        _conn = connection;
        HostList.ItemsSource = _hosts;
        ActivityGrid.ItemsSource = _activity;

        _conn.HostConnected += h => Dispatch(() => AddOrUpdateHost(h));
        _conn.HostDisconnected += id => Dispatch(() => RemoveHost(id));
        _conn.ActivityReceived += e => Dispatch(() => OnActivity(e));
        _conn.FrameReceived += f => Dispatch(() => OnFrame(f));
        _conn.TilesReceived += f => Dispatch(() => OnTiles(f));
        _conn.AlertReceived += a => Dispatch(() => OnAlertBadge(a));
        _conn.ViewersChanged += (h, v) => Dispatch(() => OnViewersChanged(h, v));
        _conn.AccessChanged += d => Dispatch(() => _ = OnAccessChangedAsync(d));

        // Populate hosts that were already connected before this console opened.
        // Live HostConnected pushes cover anything that connects afterwards; dedup in
        // AddOrUpdateHost prevents a host appearing twice if both paths deliver it.
        Loaded += async (_, _) =>
        {
            await SyncHostsAsync();
            await RefreshAlertBadgeAsync();
            await ShowAccessButtonIfOwnerAsync();
        };
        Closed += async (_, _) => await _conn.DisposeAsync();
    }

    // ---- Alerts badge + window ----

    private int _unackCount;
    private AlertsWindow? _alertsWindow;

    private async Task RefreshAlertBadgeAsync()
    {
        try
        {
            var unack = await _conn.GetAlertsAsync(acknowledged: false, limit: 500);
            _unackCount = unack.Count;
            UpdateAlertBadge();
        }
        catch { /* transient */ }
    }

    private void OnAlertBadge(Alert a)
    {
        if (!a.Acknowledged) _unackCount++;
        UpdateAlertBadge();
    }

    private void UpdateAlertBadge() =>
        AlertBadge.Text = _unackCount > 0 ? $"  {_unackCount}" : "";

    private void AlertsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_alertsWindow is { IsVisible: true })
        {
            _alertsWindow.Activate();
            return;
        }
        _alertsWindow = new AlertsWindow(_conn) { Owner = this };
        // When alerts are reviewed there, refresh the badge count on close.
        _alertsWindow.Closed += async (_, _) => await RefreshAlertBadgeAsync();
        _alertsWindow.Show();
    }

    // ---- Workstation access (owner only) ----

    private AccessWindow? _accessWindow;

    /// <summary>
    /// The access route is owner-only; a refusal is how we learn this account isn't one, so the
    /// button stays hidden rather than offering an action the server would reject.
    /// </summary>
    private async Task ShowAccessButtonIfOwnerAsync()
    {
        try
        {
            if (await _conn.GetAccessAsync() is null)
                return;

            AccessButton.Visibility = Visibility.Visible;
            await RefreshResetBadgeAsync();

            // Lockouts are rare and not urgent to the second, so a slow poll keeps the badge honest
            // without hammering an owner-only route.
            var timer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMinutes(1) };
            timer.Tick += async (_, _) => await RefreshResetBadgeAsync();
            timer.Start();
        }
        catch { /* transient; button simply stays hidden */ }
    }

    /// <summary>
    /// Shows how many viewers are waiting on a password reset, if any. Only ever called for an
    /// owner — the route is owner-only, so polling it as a viewer would just 403 on a timer.
    /// </summary>
    private async Task RefreshResetBadgeAsync()
    {
        try
        {
            var count = (await _conn.GetResetRequestsAsync()).Count;
            ResetBadge.Text = count > 0 ? $"  {count}" : "";
            AccessButton.ToolTip = count > 0
                ? $"{count} viewer(s) reported being locked out — plus viewer sign-ins and PC access"
                : "Issue viewer sign-ins and choose which PCs each may see (owner only)";
        }
        catch { /* transient */ }
    }

    // ---- My account (every role) ----

    private AccountWindow? _accountWindow;

    private void AccountButton_Click(object sender, RoutedEventArgs e)
    {
        if (_accountWindow is { IsVisible: true })
        {
            _accountWindow.Activate();
            return;
        }
        _accountWindow = new AccountWindow(_conn) { Owner = this };
        _accountWindow.Show();
    }

    private void AccessButton_Click(object sender, RoutedEventArgs e)
    {
        if (_accessWindow is { IsVisible: true })
        {
            _accessWindow.Activate();
            return;
        }
        _accessWindow = new AccessWindow(_conn) { Owner = this };
        // Resets actioned in there change the badge, so re-read it when the window closes.
        _accessWindow.Closed += async (_, _) => await RefreshResetBadgeAsync();
        _accessWindow.Show();
    }

    /// <summary>
    /// The owner changed this account's access. Drop anything showing a now-blocked PC, then
    /// re-read the host list — workstations may equally have been unblocked.
    /// </summary>
    private async Task OnAccessChangedAsync(string[] deniedHostIds)
    {
        var blocked = new HashSet<string>(deniedHostIds, StringComparer.OrdinalIgnoreCase);

        if (_viewingHostId is not null && blocked.Contains(_viewingHostId))
        {
            var lostHost = _hosts.FirstOrDefault(h => h.HostId == _viewingHostId)?.MachineName ?? _viewingHostId;
            _viewingHostId = null;
            _recording = false;
            RecordButton.Content = "● Rec";
            RecordButton.FontWeight = FontWeights.Normal;
            ScreenImage.Source = null;
            _screenBitmap = null;
            MonitorSelector.Items.Clear();
            ViewersText.Text = "";
            if (_isFullScreen) ToggleFullScreen();
            StopButton.IsEnabled = false;
            RecordButton.IsEnabled = false;
            FullScreenButton.IsEnabled = false;

            MessageBox.Show(this,
                $"Your access to “{lostHost}” was withdrawn by the owner. The live view has ended.",
                "Workstation access", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        foreach (var hostId in blocked)
            RemoveHost(hostId);

        // Re-sync against what the server will now show us.
        await SyncHostsAsync();
        ViewButton.IsEnabled = Selected is not null;
        RemoveAgentButton.IsEnabled = Selected is not null;
    }

    // ---- Who else is watching ----

    private void OnViewersChanged(string hostId, string[] viewers)
    {
        if (hostId != _viewingHostId)
            return;
        PaintViewers(viewers);
    }

    private void PaintViewers(string[] viewers)
    {
        var others = viewers
            .Where(v => !string.Equals(v, _conn.Username, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        ViewersText.Text = others.Length == 0
            ? "👁 only you"
            : $"👁 {viewers.Length} watching — {string.Join(", ", others)} and you";
    }

    /// <summary>
    /// Reconciles the host list against the server's view. Needed at startup (live HostConnected
    /// pushes only cover hosts that connect afterwards) and again whenever this account's
    /// workstation access changes, since that silently adds or removes what the server will show.
    /// </summary>
    private async Task SyncHostsAsync()
    {
        try
        {
            var hosts = await _conn.GetHostsAsync();
            Dispatch(() =>
            {
                foreach (var h in hosts)
                    AddOrUpdateHost(h);

                var visible = hosts.Select(h => h.HostId).ToHashSet();
                foreach (var stale in _hosts.Where(h => !visible.Contains(h.HostId)).ToList())
                    _hosts.Remove(stale);
            });
        }
        catch
        {
            // Snapshot failed (transient); live pushes will still populate new connections.
        }
    }

    private void Dispatch(Action a) => Dispatcher.Invoke(a);

    private void AddOrUpdateHost(HostInfo h)
    {
        if (!_hosts.Any(x => x.HostId == h.HostId))
            _hosts.Add(h);
    }

    private void RemoveHost(string hostId)
    {
        var existing = _hosts.FirstOrDefault(x => x.HostId == hostId);
        if (existing is not null) _hosts.Remove(existing);
    }

    private HostInfo? Selected => HostList.SelectedItem as HostInfo;

    private void HostList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewButton.IsEnabled = Selected is not null;
        RemoveAgentButton.IsEnabled = Selected is not null;
        _ = LoadActivityAsync(reset: true);
    }

    private async void RemoveAgentButton_Click(object sender, RoutedEventArgs e)
    {
        var host = Selected;
        if (host is null) return;

        var confirm = MessageBox.Show(
            $"Permanently uninstall the Manager Tool agent from “{host.MachineName}”?\n\n" +
            "This stops monitoring, removes auto-start, and deletes the agent from that PC. " +
            "It cannot be undone remotely — you would have to reinstall the agent to monitor this PC again.",
            "Remove agent",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            await _conn.RemoteUninstall(host.HostId);
            MessageBox.Show(
                $"Uninstall command sent to “{host.MachineName}”. It will disconnect and remove itself shortly.",
                "Remove agent", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not send the uninstall command: {ex.Message}",
                "Remove agent", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---- Activity loading with search / date-range / pagination ----

    private const int ActivityPageSize = 200;
    private int _activityOffset;

    private DateTimeOffset? FromFilter =>
        FromDate.SelectedDate is { } d ? new DateTimeOffset(d.Date, DateTimeOffset.Now.Offset) : null;
    private DateTimeOffset? ToFilter =>
        // inclusive end-of-day for the "To" date
        ToDate.SelectedDate is { } d ? new DateTimeOffset(d.Date.AddDays(1).AddTicks(-1), DateTimeOffset.Now.Offset) : null;

    private bool FilterActive =>
        !string.IsNullOrWhiteSpace(SearchBox.Text) || FromFilter is not null || ToFilter is not null;

    private void SearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
            _ = LoadActivityAsync(reset: true);
    }
    private void SearchButton_Click(object sender, RoutedEventArgs e) => _ = LoadActivityAsync(reset: true);
    private void LoadMoreButton_Click(object sender, RoutedEventArgs e) => _ = LoadActivityAsync(reset: false);

    private void ClearFilterButton_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        FromDate.SelectedDate = null;
        ToDate.SelectedDate = null;
        _ = LoadActivityAsync(reset: true);
    }

    private async Task LoadActivityAsync(bool reset)
    {
        var host = Selected;
        if (host is null)
        {
            _activity.Clear();
            ResultCount.Text = "";
            LoadMoreButton.Visibility = Visibility.Collapsed;
            return;
        }

        if (reset)
        {
            _activityOffset = 0;
            _activity.Clear();
        }

        var search = SearchBox.Text;
        try
        {
            var page = await _conn.GetActivityAsync(
                host.HostId, FromFilter, ToFilter, search, ActivityPageSize, _activityOffset);

            // Selection changed while awaiting — abandon this result.
            if (Selected?.HostId != host.HostId)
                return;

            foreach (var evt in page)             // API returns newest-first
                _activity.Add(ActivityRow.From(evt));
            _activityOffset += page.Count;

            var more = page.Count == ActivityPageSize;
            LoadMoreButton.IsEnabled = more;
            LoadMoreButton.Visibility = more ? Visibility.Visible : Visibility.Collapsed;
            ResultCount.Text = FilterActive
                ? $"{_activity.Count} match{(_activity.Count == 1 ? "" : "es")}"
                : $"{_activity.Count} events";
        }
        catch
        {
            // Fetch failed (transient); live events still populate when no filter is active.
        }
    }

    private void OnActivity(ActivityEvent e)
    {
        if (Selected is null || e.HostId != Selected.HostId)
            return;

        // Live events prepend only in the unfiltered view — otherwise they'd break the
        // filtered/paged result set the operator is looking at.
        if (FilterActive)
            return;

        _activity.Insert(0, ActivityRow.From(e));
        _activityOffset++;
        while (_activity.Count > 1000)
            _activity.RemoveAt(_activity.Count - 1);
        ResultCount.Text = $"{_activity.Count} events";
    }

    private void OnFrame(ScreenFrame f)
    {
        if (f.HostId != _viewingHostId)
            return;

        // Keep the monitor selector in sync with what the host actually has.
        if (f.MonitorCount != _knownMonitorCount)
            PopulateMonitorSelector(f.MonitorCount);

        // Only render the monitor the admin is currently viewing.
        if (f.MonitorIndex != _selectedMonitor)
            return;

        // A whole frame replaces any tiled surface — drop it so a later tile stream rebuilds cleanly.
        _screenBitmap = null;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(f.JpegBytes);
        image.EndInit();
        image.Freeze();
        ScreenImage.Source = image;
    }

    // A persistent surface that tile deltas are painted into, so only changed cells are redrawn.
    private WriteableBitmap? _screenBitmap;

    private void OnTiles(ScreenTileFrame f)
    {
        if (f.HostId != _viewingHostId)
            return;
        if (f.MonitorCount != _knownMonitorCount)
            PopulateMonitorSelector(f.MonitorCount);
        if (f.MonitorIndex != _selectedMonitor)
            return;

        // Rebuild the surface on a keyframe or a size change; deltas paint onto the existing one.
        if (f.Keyframe || _screenBitmap is null
            || _screenBitmap.PixelWidth != f.FrameWidth || _screenBitmap.PixelHeight != f.FrameHeight)
        {
            _screenBitmap = new WriteableBitmap(f.FrameWidth, f.FrameHeight, 96, 96, PixelFormats.Bgra32, null);
            ScreenImage.Source = _screenBitmap;
        }

        foreach (var tile in f.Tiles)
        {
            var col = tile.Index % f.Cols;
            var row = tile.Index / f.Cols;
            var x = col * f.TileSize;
            var y = row * f.TileSize;
            BlitTile(_screenBitmap, tile.JpegBytes, x, y, f.FrameWidth, f.FrameHeight);
        }
    }

    /// <summary>Decode one tile JPEG and copy its pixels into the live surface at (x, y).</summary>
    private static void BlitTile(WriteableBitmap target, byte[] jpeg, int x, int y, int frameW, int frameH)
    {
        var decoder = BitmapFrame.Create(new MemoryStream(jpeg),
            BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var bgra = new FormatConvertedBitmap(decoder, PixelFormats.Bgra32, null, 0);

        var w = bgra.PixelWidth;
        var h = bgra.PixelHeight;
        // A malformed or oversized tile must never write outside the surface.
        if (x + w > frameW) w = frameW - x;
        if (y + h > frameH) h = frameH - y;
        if (w <= 0 || h <= 0)
            return;

        var stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        target.WritePixels(new Int32Rect(0, 0, w, h), pixels, stride, x, y);
    }

    private void PopulateMonitorSelector(int count)
    {
        _knownMonitorCount = count;
        var previous = _selectedMonitor;

        MonitorSelector.Items.Clear();
        for (var i = 0; i < count; i++)
            MonitorSelector.Items.Add($"#{i + 1}");

        // Preserve the current selection if still valid, else fall back to the first.
        var index = previous < count ? previous : 0;
        MonitorSelector.SelectedIndex = count > 0 ? index : -1;
    }

    private void MonitorSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MonitorSelector.SelectedIndex >= 0)
            _selectedMonitor = MonitorSelector.SelectedIndex;
    }

    // ---- Full-screen viewer ----

    private void FullScreenButton_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void ScreenImage_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Double-click the screen to enter/exit full screen (only while a feed is active).
        if (e.ClickCount == 2 && (_viewingHostId is not null || _isFullScreen))
            ToggleFullScreen();
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape && _isFullScreen)
        {
            ToggleFullScreen();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Expands the live screen to fill the whole display (borderless, covers the taskbar)
    /// by hiding the side panel + toolbar; restores the normal layout on exit.
    /// </summary>
    private void ToggleFullScreen()
    {
        if (!_isFullScreen)
        {
            _prevWindowStyle = WindowStyle;
            _prevResizeMode = ResizeMode;
            _prevWindowState = WindowState;

            SidePanel.Visibility = Visibility.Collapsed;
            SideColumn.Width = new GridLength(0);
            Toolbar.Visibility = Visibility.Collapsed;

            // Borderless full-screen. Toggle state so a Maximized->Maximized change still
            // re-lays-out to cover the taskbar.
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;

            _isFullScreen = true;
        }
        else
        {
            SidePanel.Visibility = Visibility.Visible;
            SideColumn.Width = new GridLength(300);
            Toolbar.Visibility = Visibility.Visible;

            WindowStyle = _prevWindowStyle;
            ResizeMode = _prevResizeMode;
            WindowState = _prevWindowState;

            _isFullScreen = false;
        }
    }

    private async void ViewButton_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) return;
        _viewingHostId = Selected.HostId;
        _selectedMonitor = 0;
        _knownMonitorCount = 0;
        PaintViewers(new[] { _conn.Username });   // corrected by the server's ViewersChanged push
        await _conn.StartViewing(_viewingHostId);
        StopButton.IsEnabled = true;
        RecordButton.IsEnabled = true;
        FullScreenButton.IsEnabled = true;
        ViewButton.IsEnabled = false;
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewingHostId is null) return;

        if (_recording)
            await StopRecordingInternal();

        await _conn.StopViewing(_viewingHostId);
        _viewingHostId = null;
        ScreenImage.Source = null;
        _screenBitmap = null;
        MonitorSelector.Items.Clear();
        ViewersText.Text = "";
        if (_isFullScreen) ToggleFullScreen();   // don't stay full-screen with no feed
        StopButton.IsEnabled = false;
        RecordButton.IsEnabled = false;
        FullScreenButton.IsEnabled = false;
        ViewButton.IsEnabled = Selected is not null;
    }

    private async void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewingHostId is null) return;

        if (!_recording)
        {
            await _conn.StartRecording(_viewingHostId);
            _recording = true;
            RecordButton.Content = "■ Stop Rec";
            RecordButton.FontWeight = FontWeights.Bold;
        }
        else
        {
            await StopRecordingInternal();
        }
    }

    private async Task StopRecordingInternal()
    {
        if (_viewingHostId is null || !_recording) return;
        await _conn.StopRecording(_viewingHostId);
        _recording = false;
        RecordButton.Content = "● Rec";
        RecordButton.FontWeight = FontWeights.Normal;
    }
}

/// <summary>View-model row for the activity grid.</summary>
public sealed class ActivityRow
{
    public string LocalTime { get; init; } = "";
    public string ProcessName { get; init; } = "";
    public string Detail { get; init; } = "";

    public static ActivityRow From(ActivityEvent e) => new()
    {
        LocalTime = e.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
        ProcessName = e.ProcessName,
        Detail = e.Url ?? e.WindowTitle
    };
}
