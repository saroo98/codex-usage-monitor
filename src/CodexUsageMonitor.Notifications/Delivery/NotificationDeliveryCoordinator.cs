using CodexUsageMonitor.Application.Monitoring;
using CodexUsageMonitor.Core.Abstractions;
using CodexUsageMonitor.Core.Notifications;
using CodexUsageMonitor.Notifications.Native;
using CodexUsageMonitor.Persistence.Notifications;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.Notifications.Delivery;

public sealed class NotificationDeliveryCoordinator : IUsageNotificationSink
{
    private readonly INativeNotificationService _native;
    private readonly NotificationReceiptRepository _receipts;
    private readonly DeferredNotificationRepository _deferred;
    private readonly IClock _clock;
    private readonly TimeZoneInfo _timeZone;
    private readonly DeferredNotificationSignal _signal;
    private readonly ILogger<NotificationDeliveryCoordinator> _logger;

    public NotificationDeliveryCoordinator(
        INativeNotificationService native,
        NotificationReceiptRepository receipts,
        DeferredNotificationRepository deferred,
        IClock clock,
        TimeZoneInfo timeZone,
        DeferredNotificationSignal signal,
        ILogger<NotificationDeliveryCoordinator> logger)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _receipts = receipts ?? throw new ArgumentNullException(nameof(receipts));
        _deferred = deferred ?? throw new ArgumentNullException(nameof(deferred));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _timeZone = timeZone ?? throw new ArgumentNullException(nameof(timeZone));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> DeliverAsync(
        UsageTransition transition,
        QuietHoursSchedule quietHours,
        bool playSound,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transition);
        ArgumentNullException.ThrowIfNull(quietHours);
        var now = _clock.UtcNow;
        if (transition.ExpiresAtUtc <= now)
        {
            return false;
        }

        if (quietHours.IsQuiet(now, _timeZone))
        {
            var deferred = new DeferredNotification(
                transition.Identity,
                now,
                quietHours.NextEnd(now, _timeZone),
                transition.ExpiresAtUtc,
                transition.Code,
                PriorityFor(transition.Identity.EventType));
            await _deferred.UpsertAsync(deferred, cancellationToken).ConfigureAwait(false);
            _signal.Pulse();
            return false;
        }

        if (!await _receipts.TryReserveAsync(transition.Identity, now, transition.ExpiresAtUtc, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            await _native.ShowAsync(NotificationContentFactory.Create(transition, playSound), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Native notification delivery failed for {Identity}.", transition.Identity.Value);
            try
            {
                await _receipts.ReleaseAsync(transition.Identity, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception releaseException) when (releaseException is IOException or Microsoft.Data.Sqlite.SqliteException)
            {
                _logger.LogWarning(releaseException, "Notification reservation release failed for {Identity}.", transition.Identity.Value);
            }

            return false;
        }
    }

    public async Task FlushDueAsync(
        Func<DeferredNotification, UsageTransition?> resolver,
        bool playSound,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        var now = _clock.UtcNow;
        var due = await _deferred.ReadDueAsync(now, 10, cancellationToken).ConfigureAwait(false);
        foreach (var item in due)
        {
            var transition = resolver(item);
            if (transition is null || transition.ExpiresAtUtc <= now)
            {
                await _deferred.DeleteAsync(item.Identity, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var delivered = await DeliverAsync(
                transition,
                new QuietHoursSchedule(false, default, default),
                playSound,
                cancellationToken).ConfigureAwait(false);
            if (delivered || await _receipts.ExistsAsync(item.Identity, cancellationToken).ConfigureAwait(false))
            {
                await _deferred.DeleteAsync(item.Identity, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static int PriorityFor(NotificationEventType eventType) => eventType switch
    {
        NotificationEventType.Depleted => 100,
        NotificationEventType.ResetCreditAvailable => 90,
        NotificationEventType.ConnectionLost => 80,
        NotificationEventType.ThresholdCrossed => 70,
        NotificationEventType.Reset => 60,
        _ => 50,
    };
}
