namespace CodexUsageMonitor.Core.Notifications;

public static class DeferredNotificationCoalescer
{
    public static IReadOnlyList<DeferredNotification> Coalesce(
        IEnumerable<DeferredNotification> deferred,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(deferred);
        return deferred
            .Where(item => item.ExpiresAtUtc > nowUtc)
            .GroupBy(item => new
            {
                item.Identity.ProfileId,
                item.Identity.AccountStorageKey,
                item.Identity.LimitIdentity,
            })
            .Select(group => group
                .OrderByDescending(static item => item.Priority)
                .ThenByDescending(static item => item.DeferredAtUtc)
                .First())
            .OrderByDescending(static item => item.Priority)
            .ThenBy(static item => item.DeliverAfterUtc)
            .ToArray();
    }
}
