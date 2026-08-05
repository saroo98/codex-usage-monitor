using CodexUsageMonitor.Application.Diagnostics;
using CodexUsageMonitor.Application.Migration;
using CodexUsageMonitor.Application.Monitoring;
using CodexUsageMonitor.Core.Abstractions;
using CodexUsageMonitor.Core.Accounts;
using CodexUsageMonitor.Core.Monitoring;
using CodexUsageMonitor.Core.Usage;

namespace CodexUsageMonitor.UnitTests;

[TestClass]
public sealed class LegacyMigrationActionServiceTests
{
    [TestMethod]
    public async Task VerifiedBackupAndFreshSnapshotAllowReversibleTaskRetirement()
    {
        var now = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
        var migration = new FakeMigrationPort(CreateImportedMigration());
        var service = CreateService(migration, backupVerified: true, CreateSnapshot(now), CreateMonitor(now), now);

        var summary = await service.GetSummaryAsync(CancellationToken.None);
        var result = await service.RetireAsync(explicitlyConfirmed: true, CancellationToken.None);

        Assert.IsTrue(summary.BackupVerified);
        Assert.IsTrue(summary.CanRetireTasks);
        Assert.IsTrue(result.Succeeded);
        Assert.IsTrue(result.Summary.TasksRetired);
        Assert.AreEqual(1, migration.RetireCalls);
    }

    [TestMethod]
    public async Task StaleSnapshotBlocksTaskRetirementEvenWithVerifiedBackup()
    {
        var now = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
        var migration = new FakeMigrationPort(CreateImportedMigration());
        var service = CreateService(
            migration,
            backupVerified: true,
            CreateSnapshot(now.AddMinutes(-15)),
            CreateMonitor(now.AddMinutes(-15)),
            now);

        var result = await service.RetireAsync(explicitlyConfirmed: true, CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("migration.awaiting_fresh_snapshot", result.SafeStatusCode);
        Assert.AreEqual(0, migration.RetireCalls);
    }

    [TestMethod]
    public async Task CancellationDoesNotStartRetirement()
    {
        var now = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
        var migration = new FakeMigrationPort(CreateImportedMigration());
        var service = CreateService(migration, backupVerified: true, CreateSnapshot(now), CreateMonitor(now), now);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.RetireAsync(explicitlyConfirmed: true, cancellation.Token));

        Assert.AreEqual(0, migration.RetireCalls);
    }

    private static LegacyMigrationActionService CreateService(
        FakeMigrationPort migration,
        bool backupVerified,
        UsageSnapshot snapshot,
        MonitorState monitor,
        DateTimeOffset now) => new(
            migration,
            new FixedBackupVerifier(backupVerified),
            new FakeUsageProvider(snapshot, monitor),
            new FakeClock(now),
            new RejectingFailureSink());

    private static LegacyMigrationStateSnapshot CreateImportedMigration() => new(
        true,
        true,
        "5.0.0",
        "legacy.zip",
        new string('a', 64),
        [],
        null);

    private static UsageSnapshot CreateSnapshot(DateTimeOffset observedAt) => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        new AccountIdentity("account-1", null, null, null),
        observedAt,
        []);

    private static MonitorState CreateMonitor(DateTimeOffset successAt) => new(
        MonitorConnectionState.Live,
        CreateSnapshot(successAt),
        successAt,
        successAt,
        null,
        0,
        false);

    private sealed class FakeMigrationPort(LegacyMigrationStateSnapshot migration) : ILegacyMigrationStatePort
    {
        public LegacyMigrationStateSnapshot? Migration { get; } = migration;

        public LegacyTaskRetirementSnapshot? Retirement { get; private set; }

        public int RetireCalls { get; private set; }

        public Task<LegacyTaskRetirementSnapshot?> ReadRetirementAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Retirement);

        public Task<LegacyTaskRetirementSnapshot> RetireAsync(CancellationToken cancellationToken)
        {
            RetireCalls++;
            return Task.FromResult(new LegacyTaskRetirementSnapshot(true, false));
        }

        public Task<LegacyTaskRetirementSnapshot> RestoreAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new LegacyTaskRetirementSnapshot(false, false));

        public void SetRetirement(LegacyTaskRetirementSnapshot? state) => Retirement = state;
    }

    private sealed class FixedBackupVerifier(bool result) : ILegacyBackupVerificationPort
    {
        public Task<bool> VerifyAsync(string? archivePath, string? expectedSha256, CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class FakeUsageProvider(UsageSnapshot snapshot, MonitorState monitorState) : IUsageRuntimeSnapshotProvider
    {
        public UsageSnapshot? ActiveSnapshot { get; } = snapshot;

        public MonitorState ActiveMonitorState { get; } = monitorState;
    }

    private sealed class FakeClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;

        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class RejectingFailureSink : IApplicationFailureSink
    {
        public void Report(string safeCode, Exception exception, Guid? profileId = null) =>
            throw new AssertFailedException($"Unexpected failure: {safeCode}");
    }
}
