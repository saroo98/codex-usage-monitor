namespace CodexUsageMonitor.Persistence.Outbox;

public sealed record EmailOutboxItem(
    Guid Id,
    string DeduplicationKey,
    Guid ProfileId,
    string AccountKey,
    string PayloadJson,
    DateTimeOffset QueuedAtUtc,
    DateTimeOffset AvailableAtUtc,
    DateTimeOffset ExpiresAtUtc,
    int AttemptCount,
    string? LastErrorCode,
    DateTimeOffset? LeasedUntilUtc,
    DateTimeOffset? TerminalAtUtc,
    string? TerminalReason);
