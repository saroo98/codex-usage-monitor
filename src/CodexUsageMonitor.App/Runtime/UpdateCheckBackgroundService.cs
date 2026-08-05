using CodexUsageMonitor.App.Services;
using CodexUsageMonitor.Core.Abstractions;
using CodexUsageMonitor.Core.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.App.Runtime;

public sealed class UpdateCheckBackgroundService : BackgroundService
{
    private readonly ApplicationReadinessGate _readiness;
    private readonly ApplicationSettingsService _settings;
    private readonly UpdateCoordinatorService _updates;
    private readonly UpdateCheckSignal _signal;
    private readonly IClock _clock;
    private readonly ILogger<UpdateCheckBackgroundService> _logger;

    public UpdateCheckBackgroundService(
        ApplicationReadinessGate readiness,
        ApplicationSettingsService settings,
        UpdateCoordinatorService updates,
        UpdateCheckSignal signal,
        IClock clock,
        ILogger<UpdateCheckBackgroundService> logger)
    {
        _readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _updates = updates ?? throw new ArgumentNullException(nameof(updates));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _readiness.WaitAsync(stoppingToken).ConfigureAwait(false);
        _settings.Changed += OnSettingsChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var settings = _settings.Current.Updates;
                if (!settings.AutomaticChecks || settings.ManifestUri is null)
                {
                    await _signal.WaitAsync(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var interval = TimeSpan.FromHours(Math.Clamp(settings.CheckIntervalHours, 1, 168));
                var dueAt = settings.LastCheckAtUtc is null ? _clock.UtcNow : settings.LastCheckAtUtc.Value + interval;
                var wait = dueAt <= _clock.UtcNow ? TimeSpan.Zero : dueAt - _clock.UtcNow;
                var reason = await _signal.WaitAsync(wait, stoppingToken).ConfigureAwait(false);
                if (reason is not null)
                {
                    continue;
                }

                try
                {
                    var result = await _updates.CheckAsync(manual: false, stoppingToken).ConfigureAwait(false);
                    if (result.Status is UpdateRuntimeStatus.Available && _settings.Current.Updates.AutomaticDownload)
                    {
                        await _updates.PrepareAsync(stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "The update background check failed unexpectedly.");
                    await _signal.WaitAsync(TimeSpan.FromHours(1), stoppingToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _settings.Changed -= OnSettingsChanged;
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings) =>
        _signal.Pulse(UpdateWakeReason.SettingsChanged);
}
