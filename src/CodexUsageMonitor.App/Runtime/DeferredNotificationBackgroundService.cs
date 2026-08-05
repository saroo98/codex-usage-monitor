using CodexUsageMonitor.App.Services;
using CodexUsageMonitor.Core.Abstractions;
using CodexUsageMonitor.Notifications.Delivery;
using CodexUsageMonitor.Persistence.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.App.Runtime;

public sealed class DeferredNotificationBackgroundService : BackgroundService
{
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromHours(1);
    private readonly ApplicationReadinessGate _readiness;
    private readonly NotificationDeliveryCoordinator _delivery;
    private readonly DeferredNotificationResolver _resolver;
    private readonly DeferredNotificationRepository _deferred;
    private readonly NotificationReceiptRepository _receipts;
    private readonly DeferredNotificationSignal _signal;
    private readonly ApplicationSettingsService _settings;
    private readonly IClock _clock;
    private readonly ILogger<DeferredNotificationBackgroundService> _logger;

    public DeferredNotificationBackgroundService(
        ApplicationReadinessGate readiness,
        NotificationDeliveryCoordinator delivery,
        DeferredNotificationResolver resolver,
        DeferredNotificationRepository deferred,
        NotificationReceiptRepository receipts,
        DeferredNotificationSignal signal,
        ApplicationSettingsService settings,
        IClock clock,
        ILogger<DeferredNotificationBackgroundService> logger)
    {
        _readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
        _delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _deferred = deferred ?? throw new ArgumentNullException(nameof(deferred));
        _receipts = receipts ?? throw new ArgumentNullException(nameof(receipts));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _readiness.WaitAsync(stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _delivery.FlushDueAsync(
                    _resolver.Resolve,
                    _settings.Current.Notifications.PlaySound,
                    stoppingToken).ConfigureAwait(false);
                var now = _clock.UtcNow;
                await _deferred.CleanupExpiredAsync(now, stoppingToken).ConfigureAwait(false);
                await _receipts.CleanupAsync(now, stoppingToken).ConfigureAwait(false);
                var next = await _deferred.GetNextDeliverAtAsync(now, stoppingToken).ConfigureAwait(false);
                var wait = next is null ? MaintenanceInterval : next.Value - now;
                if (wait <= TimeSpan.Zero)
                {
                    continue;
                }

                if (wait > MaintenanceInterval)
                {
                    wait = MaintenanceInterval;
                }

                await _signal.WaitAsync(wait, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
            {
                _logger.LogWarning(exception, "Deferred notification maintenance failed and will be retried.");
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
