using CodexUsageMonitor.Core.Usage;

namespace CodexUsageMonitor.Core.Monitoring;

public sealed record MonitorReadResult(
    bool Succeeded,
    UsageSnapshot? Snapshot,
    string Code,
    bool IsAuthenticationFailure = false,
    bool IsCodexUnavailable = false);

public sealed class UsageStateReducer
{
    private readonly SnapshotCoherenceValidator _coherence;

    public UsageStateReducer(SnapshotCoherenceValidator coherence)
    {
        _coherence = coherence ?? throw new ArgumentNullException(nameof(coherence));
    }

    public MonitorState Apply(MonitorState state, MonitorReadResult read, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(read);
        if (!read.Succeeded || read.Snapshot is null)
        {
            var connection = read.IsAuthenticationFailure
                ? MonitorConnectionState.AuthenticationRequired
                : read.IsCodexUnavailable
                    ? MonitorConnectionState.CodexUnavailable
                    : MonitorConnectionState.Retrying;
            return state with
            {
                Connection = connection,
                LastAttemptAtUtc = nowUtc,
                SafeErrorCode = read.Code,
                ConsecutiveFailures = checked(state.ConsecutiveFailures + 1),
            };
        }

        var coherence = _coherence.Validate(read.Snapshot, state.LastValidSnapshot, nowUtc);
        if (!coherence.IsCoherent)
        {
            return state with
            {
                Connection = MonitorConnectionState.Retrying,
                LastAttemptAtUtc = nowUtc,
                SafeErrorCode = coherence.Code,
                ConsecutiveFailures = checked(state.ConsecutiveFailures + 1),
                IsProbePending = coherence.RequiresProbe,
            };
        }

        return new MonitorState(
            MonitorConnectionState.Live,
            read.Snapshot,
            nowUtc,
            nowUtc,
            null,
            0,
            false);
    }

    public static MonitorState UpdateFreshness(MonitorState state, DateTimeOffset nowUtc)
    {
        if (state.LastSuccessAtUtc is null)
        {
            return state;
        }

        var age = nowUtc - state.LastSuccessAtUtc.Value;
        var connection = age switch
        {
            _ when age < TimeSpan.Zero => MonitorConnectionState.Delayed,
            _ when age <= TimeSpan.FromMinutes(2) => MonitorConnectionState.Live,
            _ when age <= TimeSpan.FromMinutes(10) => MonitorConnectionState.Delayed,
            _ => MonitorConnectionState.Stale,
        };
        return state with { Connection = connection };
    }
}
