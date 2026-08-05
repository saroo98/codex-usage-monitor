using CodexUsageMonitor.Core.Usage;

namespace CodexUsageMonitor.Core.Notifications;

public static class ThresholdTransitionEngine
{
    public static IReadOnlyList<UsageTransition> Detect(
        UsageSnapshot? previous,
        UsageSnapshot current,
        IReadOnlyList<int> thresholds,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(thresholds);
        if (previous is null ||
            !StringComparer.Ordinal.Equals(previous.Account.StableId, current.Account.StableId))
        {
            return Array.Empty<UsageTransition>();
        }

        var transitions = new List<UsageTransition>();
        foreach (var limit in current.Limits)
        {
            var old = previous.Find(limit.Identity);
            if (old is null)
            {
                continue;
            }

            foreach (var threshold in thresholds.Distinct().OrderDescending())
            {
                if (old.RemainingPercent > threshold && limit.RemainingPercent <= threshold)
                {
                    transitions.Add(CreateThreshold(current, old, limit, threshold, nowUtc));
                }
            }

            if (DidReset(old, limit, nowUtc))
            {
                transitions.Add(new UsageTransition(
                    Identity(current, limit, NotificationEventType.Reset, ResetTransitionKey(limit)),
                    limit,
                    old,
                    nowUtc,
                    null,
                    "notification.limit_reset",
                    nowUtc.AddHours(12)));
            }
        }

        if (previous.ResetCredits <= 0 && current.ResetCredits > 0)
        {
            transitions.Add(new UsageTransition(
                new NotificationIdentity(
                    current.ProfileId,
                    current.Account.StorageKey,
                    "reset-credit",
                    NotificationEventType.ResetCreditAvailable,
                    current.ResetCredits.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                null,
                null,
                nowUtc,
                null,
                "notification.reset_credit_available",
                nowUtc.AddDays(1)));
        }

        return transitions;
    }

    private static UsageTransition CreateThreshold(
        UsageSnapshot snapshot,
        UsageLimit old,
        UsageLimit current,
        int threshold,
        DateTimeOffset nowUtc)
    {
        var eventType = threshold == 0 ? NotificationEventType.Depleted : NotificationEventType.ThresholdCrossed;
        return new UsageTransition(
            Identity(snapshot, current, eventType, threshold.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            current,
            old,
            nowUtc,
            threshold,
            threshold == 0 ? "notification.depleted" : "notification.threshold_crossed",
            nowUtc.AddHours(6));
    }

    private static NotificationIdentity Identity(
        UsageSnapshot snapshot,
        UsageLimit limit,
        NotificationEventType eventType,
        string transitionKey) =>
        new(snapshot.ProfileId, snapshot.Account.StorageKey, limit.Identity, eventType, transitionKey);

    private static bool DidReset(UsageLimit old, UsageLimit current, DateTimeOffset nowUtc) =>
        current.RemainingPercent >= old.RemainingPercent + 25m &&
        (old.ResetsAtUtc is null || old.ResetsAtUtc <= nowUtc + TimeSpan.FromMinutes(5));

    private static string ResetTransitionKey(UsageLimit limit) =>
        limit.ResetsAtUtc?.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "observed";
}
