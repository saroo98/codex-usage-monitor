using CodexUsageMonitor.App.Services;
using CodexUsageMonitor.Core.Settings;
using CodexUsageMonitor.Email.Outbox;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.App.Runtime;

public sealed class EmailOutboxBackgroundService : BackgroundService
{
    private readonly ApplicationReadinessGate _readiness;
    private readonly ApplicationSettingsService _settings;
    private readonly EmailOutboxProcessor _processor;
    private readonly EmailOutboxSignal _signal;
    private readonly ILogger<EmailOutboxBackgroundService> _logger;

    public EmailOutboxBackgroundService(
        ApplicationReadinessGate readiness,
        ApplicationSettingsService settings,
        EmailOutboxProcessor processor,
        EmailOutboxSignal signal,
        ILogger<EmailOutboxBackgroundService> logger)
    {
        _readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _readiness.WaitAsync(stoppingToken).ConfigureAwait(false);
        _settings.Changed += OnSettingsChanged;
        try
        {
            await _processor.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogCritical(exception, "The email outbox worker terminated unexpectedly.");
            throw;
        }
        finally
        {
            _settings.Changed -= OnSettingsChanged;
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings) =>
        _signal.Pulse(EmailOutboxWakeReason.ConfigurationChanged);
}
