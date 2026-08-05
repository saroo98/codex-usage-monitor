using CodexUsageMonitor.Core.Usage;

namespace CodexUsageMonitor.Core.Monitoring;

public enum MonitorConnectionState
{
    Starting,
    Live,
    Delayed,
    Stale,
    Retrying,
    AuthenticationRequired,
    CodexUnavailable,
    Faulted,
}

public sealed record MonitorState(
    MonitorConnectionState Connection,
    UsageSnapshot? LastValidSnapshot,
    DateTimeOffset? LastSuccessAtUtc,
    DateTimeOffset? LastAttemptAtUtc,
    string? SafeErrorCode,
    int ConsecutiveFailures,
    bool IsProbePending)
{
    public static MonitorState Initial { get; } =
        new(MonitorConnectionState.Starting, null, null, null, null, 0, false);

    public bool HasValidData => LastValidSnapshot is not null;
}
