using CodexUsageMonitor.Core.Diagnostics;
using CodexUsageMonitor.Core.Usage;

namespace CodexUsageMonitor.Core.Monitoring;

public sealed record SnapshotCoherenceResult(bool IsCoherent, bool RequiresProbe, string Code);

public sealed class SnapshotCoherenceValidator
{
    private static readonly TimeSpan MaximumFutureObservation = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ResetRegressionTolerance = TimeSpan.FromMinutes(2);
    private readonly IProtocolAnomalySink _anomalySink;

    public SnapshotCoherenceValidator(IProtocolAnomalySink anomalySink)
    {
        _anomalySink = anomalySink ?? throw new ArgumentNullException(nameof(anomalySink));
    }

    public SnapshotCoherenceResult Validate(
        UsageSnapshot candidate,
        UsageSnapshot? previous,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.ObservedAtUtc > nowUtc + MaximumFutureObservation)
        {
            return Reject("snapshot.future_timestamp");
        }

        if (candidate.Limits.GroupBy(static limit => limit.Identity, StringComparer.Ordinal).Any(static group => group.Count() > 1))
        {
            return Reject("snapshot.duplicate_limit_identity");
        }

        if (previous is null)
        {
            return new SnapshotCoherenceResult(true, false, "snapshot.coherent");
        }

        if (candidate.ProfileId != previous.ProfileId ||
            !StringComparer.Ordinal.Equals(candidate.Account.StableId, previous.Account.StableId))
        {
            return Reject("snapshot.identity_mismatch");
        }

        if (candidate.Sequence > 0 && previous.Sequence > 0 && candidate.Sequence < previous.Sequence)
        {
            return Reject("snapshot.sequence_regression");
        }

        foreach (var current in candidate.Limits)
        {
            var old = previous.Find(current.Identity);
            if (old?.ResetsAtUtc is null || current.ResetsAtUtc is null)
            {
                continue;
            }

            if (current.ResetsAtUtc < old.ResetsAtUtc - ResetRegressionTolerance &&
                current.ResetsAtUtc > nowUtc)
            {
                _anomalySink.Report("snapshot.reset_regression", new Dictionary<string, string>
                {
                    ["limit"] = current.Identity,
                });
                return new SnapshotCoherenceResult(false, true, "snapshot.reset_regression");
            }
        }

        return new SnapshotCoherenceResult(true, false, "snapshot.coherent");
    }

    private SnapshotCoherenceResult Reject(string code)
    {
        _anomalySink.Report(code);
        return new SnapshotCoherenceResult(false, false, code);
    }
}
