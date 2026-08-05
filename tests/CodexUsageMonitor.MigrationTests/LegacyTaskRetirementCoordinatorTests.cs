using CodexUsageMonitor.Core.Abstractions;
using CodexUsageMonitor.Migration.Execution;
using CodexUsageMonitor.Migration.Tasks;
using CodexUsageMonitor.Persistence.Paths;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexUsageMonitor.MigrationTests;

[TestClass]
public sealed class LegacyTaskRetirementCoordinatorTests
{
    [TestMethod]
    public async Task RetirePersistsCapturedStateAndRestoreReusesTheSameSnapshot()
    {
        using var fixture = new TemporaryDirectory();
        var paths = CreatePaths(fixture.Path);
        var controller = new FakeController();
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero));
        var coordinator = new LegacyTaskRetirementCoordinator(
            controller,
            paths,
            clock,
            NullLogger<LegacyTaskRetirementCoordinator>.Instance);

        var retired = await coordinator.RetireAsync(explicitlyConfirmed: true, CancellationToken.None);

        Assert.IsTrue(retired.IsRetired);
        Assert.IsTrue(retired.HasExistingTasks);
        Assert.IsTrue(File.Exists(coordinator.StatePath));
        Assert.AreEqual(1, controller.RetireCalls);

        clock.UtcNow = clock.UtcNow.AddMinutes(5);
        var restored = await coordinator.RestoreAsync(explicitlyConfirmed: true, CancellationToken.None);
        var reloaded = await coordinator.GetStateAsync(CancellationToken.None);

        Assert.IsFalse(restored.IsRetired);
        Assert.IsNotNull(restored.RestoredAtUtc);
        Assert.AreEqual(1, controller.RestoreCalls);
        Assert.AreEqual("Codex Usage Notifier", controller.LastRestoreSnapshots.Single().Name);
        Assert.IsNotNull(reloaded);
        Assert.AreEqual(restored.RetiredAtUtc, reloaded.RetiredAtUtc);
        Assert.AreEqual(restored.RestoredAtUtc, reloaded.RestoredAtUtc);
        Assert.AreEqual(restored.Snapshots.Single().Name, reloaded.Snapshots.Single().Name);
    }

    [TestMethod]
    public async Task RetireIsIdempotentWhileTasksRemainRetired()
    {
        using var fixture = new TemporaryDirectory();
        var controller = new FakeController();
        var coordinator = new LegacyTaskRetirementCoordinator(
            controller,
            CreatePaths(fixture.Path),
            new FakeClock(DateTimeOffset.UtcNow),
            NullLogger<LegacyTaskRetirementCoordinator>.Instance);

        var first = await coordinator.RetireAsync(explicitlyConfirmed: true, CancellationToken.None);
        var second = await coordinator.RetireAsync(explicitlyConfirmed: true, CancellationToken.None);

        Assert.AreEqual(first.SchemaVersion, second.SchemaVersion);
        Assert.AreEqual(first.CapturedAtUtc, second.CapturedAtUtc);
        Assert.AreEqual(first.RetiredAtUtc, second.RetiredAtUtc);
        Assert.AreEqual(first.RestoredAtUtc, second.RestoredAtUtc);
        CollectionAssert.AreEqual(first.Snapshots.ToArray(), second.Snapshots.ToArray());
        CollectionAssert.AreEqual(first.RetirementResults.ToArray(), second.RetirementResults.ToArray());
        CollectionAssert.AreEqual(first.RestoreResults.ToArray(), second.RestoreResults.ToArray());
        Assert.AreEqual(1, controller.RetireCalls);
    }

    private static AppDataPaths CreatePaths(string root) => new(
        root,
        Path.Combine(root, "settings.json"),
        Path.Combine(root, "usage.db"),
        Path.Combine(root, "logs"),
        Path.Combine(root, "cache"),
        Path.Combine(root, "updates"),
        Path.Combine(root, "support"),
        false);

    private sealed class FakeClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class FakeController : ILegacyScheduledTaskController
    {
        public int RetireCalls { get; private set; }

        public int RestoreCalls { get; private set; }

        public IReadOnlyList<LegacyTaskSnapshot> LastRestoreSnapshots { get; private set; } = [];

        public Task<IReadOnlyList<LegacyTaskSnapshot>> CaptureKnownTasksAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LegacyTaskSnapshot>>([]);

        public Task<LegacyTaskRetirementBatch> RetireKnownTasksAsync(
            bool explicitlyConfirmed,
            CancellationToken cancellationToken)
        {
            Assert.IsTrue(explicitlyConfirmed);
            RetireCalls++;
            var snapshots = new[]
            {
                new LegacyTaskSnapshot("Codex Usage Notifier", true, true, "<Task />", null),
            };
            var results = new[]
            {
                new LegacyTaskRetirementResult("Codex Usage Notifier", true, true, true, null),
            };
            return Task.FromResult(new LegacyTaskRetirementBatch(snapshots, results));
        }

        public Task<IReadOnlyList<LegacyTaskRestoreResult>> RestoreKnownTasksAsync(
            IReadOnlyList<LegacyTaskSnapshot> snapshots,
            bool explicitlyConfirmed,
            CancellationToken cancellationToken)
        {
            Assert.IsTrue(explicitlyConfirmed);
            RestoreCalls++;
            LastRestoreSnapshots = snapshots;
            return Task.FromResult<IReadOnlyList<LegacyTaskRestoreResult>>(
                [new LegacyTaskRestoreResult("Codex Usage Notifier", true, true, null)]);
        }

        public Task<IReadOnlyList<LegacyTaskResult>> RemoveKnownTasksAsync(
            bool explicitlyConfirmed,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LegacyTaskResult>>([]);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"CodexUsageMonitorTests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
