using CodexUsageMonitor.Application.Diagnostics;
using CodexUsageMonitor.Application.Monitoring;
using CodexUsageMonitor.Application.Runtime;
using CodexUsageMonitor.Application.Settings;
using CodexUsageMonitor.Core.Abstractions;
using CodexUsageMonitor.Core.Accounts;
using CodexUsageMonitor.Core.Monitoring;
using CodexUsageMonitor.Core.Notifications;
using CodexUsageMonitor.Core.Profiles;
using CodexUsageMonitor.Core.Settings;
using CodexUsageMonitor.Core.Usage;

namespace CodexUsageMonitor.UnitTests;

[TestClass]
public sealed class ApplicationMonitoringTests
{
    [TestMethod]
    public async Task FailureStatePreservesLastConfirmedSnapshot()
    {
        var fixture = new StateFixture();
        var profile = ProfileDefinition.CreateDefault();
        var snapshot = CreateSnapshot(profile.Id, "account-a", 25m);

        await fixture.State.SnapshotReceivedAsync(profile, snapshot, CancellationToken.None);
        await fixture.State.StateChangedAsync(
            profile,
            new MonitorState(MonitorConnectionState.Stale, snapshot, snapshot.ObservedAtUtc, snapshot.ObservedAtUtc.AddMinutes(2), "timeout", 1, false),
            CancellationToken.None);

        Assert.AreSame(snapshot, fixture.State.ActiveSnapshot);
        Assert.AreEqual(MonitorConnectionState.Stale, fixture.State.ActiveMonitorState.Connection);
        Assert.AreEqual(1, fixture.History.Snapshots.Count);
    }

    [TestMethod]
    public async Task AccountChangeDoesNotEmitThresholdTransitionFromPriorAccount()
    {
        var fixture = new StateFixture();
        var profile = ProfileDefinition.CreateDefault();

        await fixture.State.SnapshotReceivedAsync(profile, CreateSnapshot(profile.Id, "account-a", 10m), CancellationToken.None);
        await fixture.State.SnapshotReceivedAsync(profile, CreateSnapshot(profile.Id, "account-b", 99m), CancellationToken.None);

        Assert.AreEqual(0, fixture.Notifications.Transitions.Count);
        Assert.AreEqual(0, fixture.Email.Transitions.Count);
        Assert.AreEqual("account-b", fixture.State.ActiveSnapshot?.Account.StableId);
    }

    [TestMethod]
    public async Task ConcurrentRestartsAreSerialized()
    {
        var settings = new SettingsSnapshot(new AppSettings());
        var monitors = new SerialMonitorLifecycle();
        var failures = new FailureSink();
        var coordinator = new ProfileMonitoringCoordinatorService(settings, monitors, failures);
        coordinator.Start(CancellationToken.None);

        await Task.WhenAll(
            coordinator.RestartAllAsync(CancellationToken.None),
            coordinator.RestartAllAsync(CancellationToken.None));

        Assert.AreEqual(2, monitors.StopCalls);
        Assert.AreEqual(1, monitors.MaximumConcurrentStops);
        Assert.AreEqual(0, failures.Codes.Count);
        await coordinator.DisposeAsync();
    }

    private static UsageSnapshot CreateSnapshot(Guid profileId, string accountId, decimal usedPercent) =>
        new(
            profileId,
            new AccountIdentity(accountId, null, accountId, null),
            new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero),
            [new UsageLimit("primary", LimitKind.FiveHour, "Primary", usedPercent, null)]);

    private sealed class StateFixture
    {
        public StateFixture()
        {
            State = new UsageApplicationState(History, Notifications, Email, Settings, new FixedClock(), new InlineDispatcher(), Failures);
        }

        public HistoryWriter History { get; } = new();
        public NotificationSink Notifications { get; } = new();
        public EmailSink Email { get; } = new();
        public SettingsSnapshot Settings { get; } = new(new AppSettings());
        public FailureSink Failures { get; } = new();
        public UsageApplicationState State { get; }
    }

    private sealed class HistoryWriter : IUsageHistoryWriter
    {
        public List<UsageSnapshot> Snapshots { get; } = [];

        public Task RecordAsync(UsageSnapshot snapshot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Snapshots.Add(snapshot);
            return Task.CompletedTask;
        }
    }

    private sealed class NotificationSink : IUsageNotificationSink
    {
        public List<UsageTransition> Transitions { get; } = [];

        public Task<bool> DeliverAsync(UsageTransition transition, QuietHoursSchedule quietHours, bool playSound, CancellationToken cancellationToken)
        {
            Transitions.Add(transition);
            return Task.FromResult(true);
        }
    }

    private sealed class EmailSink : IUsageEmailSink
    {
        public bool IsConfigured => true;
        public List<UsageTransition> Transitions { get; } = [];

        public Task<bool> QueueAsync(UsageTransition transition, UsageSnapshot snapshot, CancellationToken cancellationToken)
        {
            Transitions.Add(transition);
            return Task.FromResult(true);
        }
    }

    private sealed class SettingsSnapshot(AppSettings current) : IApplicationSettingsSnapshot
    {
        public AppSettings Current { get; private set; } = current;
        public event EventHandler<AppSettings>? Changed;

        public void Set(AppSettings settings)
        {
            Current = settings;
            Changed?.Invoke(this, settings);
        }
    }

    private sealed class InlineDispatcher : IApplicationEventDispatcher
    {
        public void Post(Action action) => action();
    }

    private sealed class FailureSink : IApplicationFailureSink
    {
        public List<string> Codes { get; } = [];

        public void Report(string safeCode, Exception exception, Guid? profileId = null) => Codes.Add(safeCode);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class SerialMonitorLifecycle : IProfileMonitorLifecycle
    {
        private int _activeStops;

        public IReadOnlyCollection<Guid> RunningProfileIds { get; private set; } = [];
        public int StopCalls { get; private set; }
        public int MaximumConcurrentStops { get; private set; }

        public void Reconcile(IEnumerable<ProfileDefinition> profiles, CancellationToken applicationToken) =>
            RunningProfileIds = profiles.Where(static profile => profile.Enabled && profile.MonitorInBackground).Select(static profile => profile.Id).ToArray();

        public int RequestRefreshAll(bool manual = true) => RunningProfileIds.Count;

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            StopCalls++;
            var active = Interlocked.Increment(ref _activeStops);
            MaximumConcurrentStops = Math.Max(MaximumConcurrentStops, active);
            try
            {
                await Task.Delay(25, cancellationToken);
                RunningProfileIds = [];
            }
            finally
            {
                Interlocked.Decrement(ref _activeStops);
            }
        }
    }
}
