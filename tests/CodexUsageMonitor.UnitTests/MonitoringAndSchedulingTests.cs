using CodexUsageMonitor.Core.Accounts;
using CodexUsageMonitor.Core.Diagnostics;
using CodexUsageMonitor.Core.Monitoring;
using CodexUsageMonitor.Core.Scheduling;
using CodexUsageMonitor.Core.Usage;

namespace CodexUsageMonitor.UnitTests;

[TestClass]
public sealed class MonitoringAndSchedulingTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 5, 12, 34, 45, TimeSpan.Zero);

    [TestMethod]
    public void CoherenceRejectsFutureDuplicateIdentityAndSequenceRegression()
    {
        var sink = new RecordingAnomalySink();
        var validator = new SnapshotCoherenceValidator(sink);
        var previous = Snapshot(2, Now, [Limit("a", 20m, Now.AddHours(1))]);

        var future = validator.Validate(Snapshot(2, Now.AddMinutes(6), [Limit("a", 21m, Now.AddHours(1))]), previous, Now);
        var duplicate = validator.Validate(Snapshot(2, Now, [Limit("a", 21m, null), Limit("a", 22m, null)]), previous, Now);
        var regression = validator.Validate(Snapshot(1, Now, [Limit("a", 21m, Now.AddHours(1))]), previous, Now);

        Assert.AreEqual("snapshot.future_timestamp", future.Code);
        Assert.AreEqual("snapshot.duplicate_limit_identity", duplicate.Code);
        Assert.AreEqual("snapshot.sequence_regression", regression.Code);
        CollectionAssert.Contains(sink.Codes, future.Code);
        CollectionAssert.Contains(sink.Codes, duplicate.Code);
        CollectionAssert.Contains(sink.Codes, regression.Code);
    }

    [TestMethod]
    public void ResetTimestampRegressionRequestsConfirmationProbe()
    {
        var sink = new RecordingAnomalySink();
        var validator = new SnapshotCoherenceValidator(sink);
        var previous = Snapshot(1, Now, [Limit("a", 20m, Now.AddHours(2))]);
        var candidate = Snapshot(2, Now.AddSeconds(1), [Limit("a", 21m, Now.AddHours(1))]);

        var result = validator.Validate(candidate, previous, Now);

        Assert.IsFalse(result.IsCoherent);
        Assert.IsTrue(result.RequiresProbe);
        Assert.AreEqual("snapshot.reset_regression", result.Code);
        Assert.AreEqual("a", sink.Contexts.Single()["limit"]);
    }

    [TestMethod]
    public void ReducerPreservesLastConfirmedSnapshotAcrossFailuresAndClassifiesCause()
    {
        var reducer = new UsageStateReducer(new SnapshotCoherenceValidator(NullProtocolAnomalySink.Instance));
        var snapshot = Snapshot(1, Now, [Limit("a", 20m, null)]);
        var live = reducer.Apply(
            MonitorState.Initial,
            new MonitorReadResult(true, snapshot, "ok"),
            Now);
        var failed = reducer.Apply(
            live,
            new MonitorReadResult(false, null, "codex.missing", IsCodexUnavailable: true),
            Now.AddSeconds(5));

        Assert.AreEqual(MonitorConnectionState.CodexUnavailable, failed.Connection);
        Assert.AreSame(snapshot, failed.LastValidSnapshot);
        Assert.AreEqual(Now, failed.LastSuccessAtUtc);
        Assert.AreEqual(1, failed.ConsecutiveFailures);
        Assert.AreEqual("codex.missing", failed.SafeErrorCode);
    }

    [TestMethod]
    [DataRow(-1, MonitorConnectionState.Delayed)]
    [DataRow(0, MonitorConnectionState.Live)]
    [DataRow(2, MonitorConnectionState.Live)]
    [DataRow(3, MonitorConnectionState.Delayed)]
    [DataRow(10, MonitorConnectionState.Delayed)]
    [DataRow(11, MonitorConnectionState.Stale)]
    public void FreshnessUsesLastConfirmedSuccessBoundary(int minutes, MonitorConnectionState expected)
    {
        var reducer = new UsageStateReducer(new SnapshotCoherenceValidator(NullProtocolAnomalySink.Instance));
        var state = MonitorState.Initial with { LastSuccessAtUtc = Now };

        var updated = UsageStateReducer.UpdateFreshness(state, Now.AddMinutes(minutes));

        Assert.AreEqual(expected, updated.Connection);
    }

    [TestMethod]
    public void HiddenWidgetPausesCountdownOnlyWakeups()
    {
        var state = MonitorState.Initial;

        Assert.IsNull(AdaptiveUiScheduler.NextDelay(
            state,
            isWidgetVisible: false,
            isHovering: false,
            Now,
            Now.AddMinutes(5)));
    }

    [TestMethod]
    public void VisibleResetUnderTwentyFourHoursUsesNextMinuteBoundary()
    {
        var state = MonitorState.Initial;

        var delay = AdaptiveUiScheduler.NextDelay(
            state,
            isWidgetVisible: true,
            isHovering: false,
            Now,
            Now.AddHours(2));

        Assert.AreEqual(TimeSpan.FromSeconds(15), delay);
    }

    [TestMethod]
    public void VisibleResetBeyondTwentyFourHoursUsesNextHourBoundary()
    {
        var state = MonitorState.Initial;

        var delay = AdaptiveUiScheduler.NextDelay(
            state,
            isWidgetVisible: true,
            isHovering: false,
            Now,
            Now.AddDays(2));

        Assert.AreEqual(TimeSpan.FromMinutes(25).Add(TimeSpan.FromSeconds(15)), delay);
    }

    [TestMethod]
    public void FreshnessBoundaryWinsOverLaterCountdownBoundary()
    {
        var state = MonitorState.Initial with { LastSuccessAtUtc = Now.AddMinutes(-1).AddSeconds(-50) };

        var delay = AdaptiveUiScheduler.NextDelay(
            state,
            isWidgetVisible: true,
            isHovering: false,
            Now,
            Now.AddHours(2));

        Assert.AreEqual(TimeSpan.FromSeconds(10), delay);
    }

    [TestMethod]
    [DataRow(1, 0.0, 0.8)]
    [DataRow(1, 1.0, 1.2)]
    [DataRow(8, 0.0, 96.0)]
    [DataRow(100, 1.0, 144.0)]
    public void RetryBackoffIsBoundedAndDeterministic(int failures, double random, double expectedSeconds)
    {
        var policy = new RetryBackoffPolicy(new FixedRandom(random));

        Assert.AreEqual(expectedSeconds, policy.DelayFor(failures).TotalSeconds, 0.0001);
    }

    private static UsageSnapshot Snapshot(long sequence, DateTimeOffset observed, IReadOnlyList<UsageLimit> limits) =>
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"), new AccountIdentity("account", null, null, null), observed, limits, sequence: sequence);

    private static UsageLimit Limit(string id, decimal used, DateTimeOffset? reset) =>
        new(id, LimitKind.Dynamic, id, used, reset);

    private sealed class FixedRandom(double value) : IRandomSource
    {
        public double NextDouble() => value;
    }

    private sealed class RecordingAnomalySink : IProtocolAnomalySink
    {
        public List<string> Codes { get; } = [];
        public List<IReadOnlyDictionary<string, string>> Contexts { get; } = [];

        public void Report(string code, IReadOnlyDictionary<string, string>? safeContext = null)
        {
            Codes.Add(code);
            if (safeContext is not null)
            {
                Contexts.Add(safeContext);
            }
        }
    }
}
