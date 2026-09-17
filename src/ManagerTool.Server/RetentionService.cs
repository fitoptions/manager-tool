namespace ManagerTool.Server;

/// <summary>Strongly-typed view of the "ManagerTool:Retention" config section.</summary>
public sealed class RetentionOptions
{
    /// <summary>How long activity events are kept. 0 or negative disables purging.</summary>
    public int Days { get; set; } = 90;

    /// <summary>How often the purge sweep runs.</summary>
    public int SweepHours { get; set; } = 6;
}

/// <summary>
/// Periodically deletes activity older than the retention window. Runs an initial sweep
/// shortly after startup, then on the configured interval. Screen frames are never
/// persisted, so this only governs the activity table.
/// </summary>
public sealed class RetentionService : BackgroundService
{
    private readonly ActivityStore _store;
    private readonly RetentionOptions _options;
    private readonly Recorder _recorder;
    private readonly RecordingOptions _recordingOptions;
    private readonly ILogger<RetentionService> _logger;

    public RetentionService(
        ActivityStore store,
        RetentionOptions options,
        Recorder recorder,
        RecordingOptions recordingOptions,
        ILogger<RetentionService> logger)
    {
        _store = store;
        _options = options;
        _recorder = recorder;
        _recordingOptions = recordingOptions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.Days <= 0 && _recordingOptions.RetentionDays <= 0)
        {
            _logger.LogInformation("Retention disabled; activity and recordings are kept indefinitely.");
            return;
        }

        var interval = TimeSpan.FromHours(Math.Max(1, _options.SweepHours));

        // Small initial delay so startup isn't blocked by the first sweep.
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            SweepActivity();
            SweepRecordings();
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void SweepActivity()
    {
        if (_options.Days <= 0)
            return;
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-_options.Days);
            var removed = _store.PurgeOlderThan(cutoff);
            if (removed > 0)
                _logger.LogInformation("Retention removed {Count} activity rows older than {Cutoff:o}.",
                    removed, cutoff);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Activity retention sweep failed; will retry next interval.");
        }
    }

    private void SweepRecordings()
    {
        if (_recordingOptions.RetentionDays <= 0)
            return;
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-_recordingOptions.RetentionDays);
            var removed = RecordingCleanup.PurgeOlderThan(_recorder.Root, cutoff);
            if (removed > 0)
                _logger.LogInformation("Retention removed {Count} recorded session(s) older than {Cutoff:o}.",
                    removed, cutoff);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recording retention sweep failed; will retry next interval.");
        }
    }
}
