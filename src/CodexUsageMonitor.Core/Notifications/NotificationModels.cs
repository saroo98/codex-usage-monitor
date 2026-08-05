using CodexUsageMonitor.Core.Usage;

namespace CodexUsageMonitor.Core.Notifications;

public enum NotificationEventType
{
    ThresholdCrossed,
    Depleted,
    Reset,
    ConnectionLost,
    ConnectionRestored,
    ResetCreditAvailable,
}

public sealed record NotificationIdentity(
    Guid ProfileId,
    string AccountStorageKey,
    string LimitIdentity,
    NotificationEventType EventType,
    string TransitionKey)
{
    public string Value => string.Join(':',
        ProfileId.ToString("N"),
        AccountStorageKey,
        LimitIdentity,
        EventType.ToString(),
        TransitionKey);
}

public sealed record UsageTransition(
    NotificationIdentity Identity,
    UsageLimit? Current,
    UsageLimit? Previous,
    DateTimeOffset OccurredAtUtc,
    int? Threshold,
    string Code,
    DateTimeOffset ExpiresAtUtc);

public sealed record DeferredNotification(
    NotificationIdentity Identity,
    DateTimeOffset DeferredAtUtc,
    DateTimeOffset DeliverAfterUtc,
    DateTimeOffset ExpiresAtUtc,
    string PayloadCode,
    int Priority);
