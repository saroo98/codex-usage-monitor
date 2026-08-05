using System.Globalization;
using CodexUsageMonitor.App.Services;
using CodexUsageMonitor.Core.Notifications;

namespace CodexUsageMonitor.App.Runtime;

public sealed class DeferredNotificationResolver
{
    private readonly UsageApplicationState _state;

    public DeferredNotificationResolver(UsageApplicationState state)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    public UsageTransition? Resolve(DeferredNotification item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!_state.TryGetSnapshot(item.Identity.ProfileId, out var snapshot) ||
            !string.Equals(snapshot.Account.StorageKey, item.Identity.AccountStorageKey, StringComparison.Ordinal))
        {
            return null;
        }

        var current = snapshot.Find(item.Identity.LimitIdentity);
        if (item.Identity.EventType is NotificationEventType.ThresholdCrossed or NotificationEventType.Depleted or NotificationEventType.Reset && current is null)
        {
            return null;
        }

        int? threshold = null;
        if (item.Identity.EventType is NotificationEventType.ThresholdCrossed or NotificationEventType.Depleted &&
            int.TryParse(item.Identity.TransitionKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            threshold = parsed;
        }

        return new UsageTransition(
            item.Identity,
            current,
            null,
            item.DeferredAtUtc,
            threshold,
            item.PayloadCode,
            item.ExpiresAtUtc);
    }
}
