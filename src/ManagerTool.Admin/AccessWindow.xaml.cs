using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ManagerTool.Shared;

namespace ManagerTool.Admin;

/// <summary>
/// Owner-only editor for which PCs each viewer may see. Access is a deny-list, so a ticked box
/// means "allowed" and leaving every box ticked keeps the viewer unrestricted — including for
/// workstations that enrol later. Each toggle saves straight away; the server is the enforcer,
/// this window is only the control surface.
/// </summary>
public partial class AccessWindow : Window
{
    private readonly AdminConnection _conn;
    private readonly ObservableCollection<AccessViewerVm> _viewers = new();

    public AccessWindow(AdminConnection connection)
    {
        InitializeComponent();
        _conn = connection;
        ViewerList.ItemsSource = _viewers;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        StatusText.Text = "Loading…";
        await LoadResetQueueAsync();
        var matrix = await _conn.GetAccessAsync();

        _viewers.Clear();
        if (matrix is null)
        {
            ShowEmpty("Only the owner account can manage workstation access.");
            return;
        }
        if (matrix.Viewers.Length == 0)
        {
            ShowEmpty("No viewer accounts yet. Approved admins appear here once they exist.");
            return;
        }
        if (matrix.Hosts.Length == 0)
        {
            ShowEmpty("No workstations have enrolled yet, so there is nothing to grant or block.");
            return;
        }

        foreach (var viewer in matrix.Viewers)
            _viewers.Add(new AccessViewerVm(viewer, matrix.Hosts));

        Scroller.Visibility = Visibility.Visible;
        EmptyText.Visibility = Visibility.Collapsed;
        StatusText.Text = $"{matrix.Viewers.Length} viewer(s) · {matrix.Hosts.Length} workstation(s)";
    }

    private void ShowEmpty(string message)
    {
        EmptyText.Text = message;
        EmptyText.Visibility = Visibility.Visible;
        Scroller.Visibility = Visibility.Collapsed;
        StatusText.Text = "";
    }

    // ---- Locked-out viewers awaiting an owner reset ----

    private readonly ObservableCollection<ResetTicketVm> _resets = new();

    private async Task LoadResetQueueAsync()
    {
        if (ResetQueueList.ItemsSource is null)
            ResetQueueList.ItemsSource = _resets;

        var tickets = await _conn.GetResetRequestsAsync();
        _resets.Clear();
        foreach (var t in tickets)
            _resets.Add(new ResetTicketVm(t));

        ResetQueuePanel.Visibility = _resets.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void SetResetPassword_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ResetTicketVm ticket }) return;

        // Pre-fill a generated password so the owner isn't tempted to invent a weak one.
        var suggested = GeneratePassword();
        var dialog = new ResetPasswordPrompt(ticket.Username, suggested) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var error = await _conn.ResetViewerPasswordAsync(ticket.Username, dialog.NewPassword);
        if (error is not null)
        {
            StatusText.Text = error;
            return;
        }

        await LoadResetQueueAsync();   // the ticket clears itself once actioned
        StatusText.Text = $"Password set for {ticket.Username} — give it to them now.";
        MessageBox.Show(this,
            $"New password for “{ticket.Username}”:\n\n{dialog.NewPassword}\n\n" +
            "Hand this over now — it is not stored anywhere you can read it back.",
            "Password set", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void DismissReset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ResetTicketVm ticket }) return;

        var confirm = MessageBox.Show(this,
            $"Dismiss the lockout report from “{ticket.Username}” without changing their password?",
            "Dismiss request", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        await _conn.DismissResetRequestAsync(ticket.Username);
        await LoadResetQueueAsync();
        StatusText.Text = $"Dismissed the request from {ticket.Username}.";
    }

    // ---- Issuing viewer sign-ins ----

    /// <summary>
    /// Ambiguity-free alphabet — no O/0 or l/1/I, because these passwords get read off a screen
    /// and typed by hand.
    /// </summary>
    private static string GeneratePassword(int length = 14)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(length);
        return string.Concat(bytes.Select(b => alphabet[b % alphabet.Length]));
    }

    private void GeneratePassword_Click(object sender, RoutedEventArgs e) =>
        NewPassBox.Text = GeneratePassword();

    private async void CreateViewer_Click(object sender, RoutedEventArgs e)
    {
        var username = NewUserBox.Text.Trim();
        var password = NewPassBox.Text;

        CreateMessage.Foreground = Brushes.IndianRed;
        if (username.Length == 0) { CreateMessage.Text = "Username is required."; return; }
        if (password.Length < 8) { CreateMessage.Text = "Password must be at least 8 characters."; return; }

        var error = await _conn.CreateAdminAsync(username, password);
        if (error is not null) { CreateMessage.Text = error; return; }

        CreateMessage.Foreground = Brushes.MediumSeaGreen;
        CreateMessage.Text = $"Created “{username}”. Give them this password now — it is not shown again.";
        NewUserBox.Clear();
        NewPassBox.Clear();

        await LoadAsync();   // the new viewer appears below, restrictable straight away
    }

    /// <summary>
    /// A box was ticked or unticked. The bound property has already changed, so we persist the
    /// viewer's whole deny-list and roll the box back if the server rejects it.
    /// </summary>
    private async void HostCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: AccessHostVm host }) return;

        var viewer = _viewers.FirstOrDefault(v => v.Hosts.Contains(host));
        if (viewer is null) return;

        viewer.RefreshSummary();
        StatusText.Text = $"Saving {viewer.Username}…";

        var saved = await _conn.SetAccessAsync(viewer.Username, viewer.DeniedHostIds());
        if (saved)
        {
            StatusText.Text = host.Allowed
                ? $"{viewer.Username} can now see {host.Label}."
                : $"{viewer.Username} is now blocked from {host.Label}.";
            return;
        }

        host.Allowed = !host.Allowed;     // put the tick back — nothing was persisted
        viewer.RefreshSummary();
        StatusText.Text = "Could not save that change.";
        MessageBox.Show(this, "The server rejected that access change. Nothing was saved.",
            "Workstation access", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}

/// <summary>A lockout report as the queue displays it.</summary>
public sealed class ResetTicketVm
{
    public ResetTicketVm(PasswordResetTicket ticket)
    {
        Username = ticket.Username;
        When = ticket.RequestedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    public string Username { get; }
    public string When { get; }
}

/// <summary>One viewer row: the account plus a tick per workstation.</summary>
public sealed class AccessViewerVm : INotifyPropertyChanged
{
    public string Username { get; }
    public Visibility PendingVisibility { get; }
    public ObservableCollection<AccessHostVm> Hosts { get; } = new();

    public AccessViewerVm(AccessViewer viewer, IReadOnlyList<AccessHost> allHosts)
    {
        Username = viewer.Username;
        PendingVisibility = viewer.Status == "pending" ? Visibility.Visible : Visibility.Collapsed;

        var denied = new HashSet<string>(viewer.DeniedHostIds, StringComparer.OrdinalIgnoreCase);
        foreach (var host in allHosts)
            Hosts.Add(new AccessHostVm(host, allowed: !denied.Contains(host.HostId)));
    }

    public IEnumerable<string> DeniedHostIds() => Hosts.Where(h => !h.Allowed).Select(h => h.HostId);

    public string Summary
    {
        get
        {
            var blocked = Hosts.Count(h => !h.Allowed);
            return blocked == 0
                ? $"Sees all {Hosts.Count} workstation(s), including any enrolled later."
                : $"Blocked from {blocked} of {Hosts.Count} workstation(s).";
        }
    }

    public void RefreshSummary() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>One workstation tick-box within a viewer row.</summary>
public sealed class AccessHostVm : INotifyPropertyChanged
{
    private bool _allowed;

    public AccessHostVm(AccessHost host, bool allowed)
    {
        HostId = host.HostId;
        Label = host.DisplayName;
        Online = host.Online;
        Tooltip = host.Online
            ? $"{host.MachineName} · online now"
            : $"{host.MachineName} · last seen {host.LastSeenUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
        _allowed = allowed;
    }

    public string HostId { get; }
    public string Label { get; }
    public bool Online { get; }
    public string Tooltip { get; }

    public string StatusDot => "●";
    public Brush StatusBrush => Online ? Brushes.MediumSeaGreen : Brushes.Gray;

    public bool Allowed
    {
        get => _allowed;
        set
        {
            if (_allowed == value) return;
            _allowed = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Allowed)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
