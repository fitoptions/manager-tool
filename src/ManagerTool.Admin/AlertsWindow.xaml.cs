using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using ManagerTool.Shared;

namespace ManagerTool.Admin;

/// <summary>
/// Alerts console: historical + live compliance alerts with severity colouring and
/// acknowledgement. Subscribes to the shared AdminConnection's live alert stream.
/// </summary>
public partial class AlertsWindow : Window
{
    private readonly AdminConnection _conn;
    private readonly ObservableCollection<AlertRow> _rows = new();

    public AlertsWindow(AdminConnection conn)
    {
        InitializeComponent();
        _conn = conn;
        Grid.ItemsSource = _rows;

        _conn.AlertReceived += OnLiveAlert;
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => _conn.AlertReceived -= OnLiveAlert;
    }

    private void OnLiveAlert(Alert a) => Dispatcher.Invoke(() =>
    {
        if (UnackOnly.IsChecked == true && a.Acknowledged) return;
        _rows.Insert(0, AlertRow.From(a));
        UpdateCount();
    });

    private async Task LoadAsync()
    {
        try
        {
            bool? ackFilter = UnackOnly.IsChecked == true ? false : null;
            var alerts = await _conn.GetAlertsAsync(acknowledged: ackFilter);
            _rows.Clear();
            foreach (var a in alerts)
                _rows.Add(AlertRow.From(a));
            UpdateCount();
        }
        catch
        {
            // transient; user can Refresh
        }
    }

    private void UpdateCount()
    {
        var unack = _rows.Count(r => r.CanAck);
        CountText.Text = $"{_rows.Count} shown · {unack} unacknowledged";
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();
    private async void Filter_Changed(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void Ack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: long id }) return;
        var row = _rows.FirstOrDefault(r => r.Id == id);
        if (row is null || !row.CanAck) return;

        if (await _conn.AcknowledgeAlertAsync(id))
        {
            row.MarkAcknowledged();
            if (UnackOnly.IsChecked == true)
                _rows.Remove(row);
            Grid.Items.Refresh();
            UpdateCount();
        }
    }
}

/// <summary>View-model row for the alerts grid.</summary>
public sealed class AlertRow
{
    public long Id { get; init; }
    public string LocalTime { get; init; } = "";
    public string Machine { get; init; } = "";
    public string Message { get; init; } = "";
    public string SeverityLabel { get; private set; } = "";
    public Brush SeverityBrush { get; private set; } = Brushes.Gray;
    public bool CanAck { get; private set; }
    public string AckLabel { get; private set; } = "";

    public static AlertRow From(Alert a) => new()
    {
        Id = a.Id,
        LocalTime = a.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
        Machine = a.MachineName,
        Message = a.Message,
        SeverityLabel = a.Severity.ToUpperInvariant(),
        SeverityBrush = BrushFor(a.Severity),
        CanAck = !a.Acknowledged,
        AckLabel = a.Acknowledged ? "Acknowledged" : "Acknowledge",
    };

    public void MarkAcknowledged()
    {
        CanAck = false;
        AckLabel = "Acknowledged";
    }

    private static Brush BrushFor(string severity) => severity switch
    {
        Severities.Critical => new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B)),
        Severities.Warning => new SolidColorBrush(Color.FromRgb(0xC0, 0x8A, 0x1E)),
        _ => new SolidColorBrush(Color.FromRgb(0x3C, 0x6E, 0x8F)),
    };
}
