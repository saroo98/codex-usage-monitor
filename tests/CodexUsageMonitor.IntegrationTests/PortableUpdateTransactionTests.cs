using CodexUsageMonitor.Updater.Install;

namespace CodexUsageMonitor.IntegrationTests;

[TestClass]
public sealed class PortableUpdateTransactionTests
{
    [TestMethod]
    public async Task HealthyUpdatedProcessCommitsAndRemovesRollbackCopy()
    {
        using var fixture = await PortableUpdateTestFixture.CreateAsync();
        await fixture.WritePreparedJournalAsync();
        var runtime = new FakeUpdateTransactionRuntime(fixture.Now);
        runtime.OnDelayAsync = async () =>
        {
            var process = runtime.LastStartedProcess;
            Assert.IsNotNull(process);
            await StartupHealthMarker.WriteAsync(
                await fixture.ReadJournalAsync(),
                process!.ProcessId,
                process.StartedAtUtc,
                runtime.UtcNow,
                CancellationToken.None);
        };
        var transaction = new PortableUpdateTransaction(
            runtime,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5));

        var result = await transaction.ExecuteAsync(fixture.Request, CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsFalse(result.RolledBack);
        Assert.AreEqual("target-application", await File.ReadAllTextAsync(fixture.InstalledApplication));
        Assert.IsFalse(Directory.Exists(fixture.Request.BackupDirectory));
        Assert.AreEqual(UpdateTransactionState.Committed, (await fixture.ReadJournalAsync()).State);
        CollectionAssert.AreEqual(
            new[] { UpdateApplicationLaunchMode.AfterUpdate },
            runtime.LaunchModes.ToArray());
    }

    [TestMethod]
    public async Task MissingHealthMarkerRestoresPriorVersionAndRestartsRollback()
    {
        using var fixture = await PortableUpdateTestFixture.CreateAsync();
        await fixture.WritePreparedJournalAsync();
        var runtime = new FakeUpdateTransactionRuntime(fixture.Now)
        {
            AdvancePerDelay = TimeSpan.FromSeconds(2),
        };
        var transaction = new PortableUpdateTransaction(
            runtime,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));

        var result = await transaction.ExecuteAsync(fixture.Request, CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.RolledBack);
        Assert.AreEqual("current-application", await File.ReadAllTextAsync(fixture.InstalledApplication));
        Assert.AreEqual(UpdateTransactionState.RolledBack, (await fixture.ReadJournalAsync()).State);
        CollectionAssert.AreEqual(
            new[] { UpdateApplicationLaunchMode.AfterUpdate, UpdateApplicationLaunchMode.RolledBack },
            runtime.LaunchModes.ToArray());
    }

    [TestMethod]
    public async Task HealthMarkerFromDifferentProcessIsRejectedAndRolledBack()
    {
        using var fixture = await PortableUpdateTestFixture.CreateAsync();
        await fixture.WritePreparedJournalAsync();
        var runtime = new FakeUpdateTransactionRuntime(fixture.Now);
        runtime.OnDelayAsync = async () =>
        {
            var process = runtime.LastStartedProcess;
            Assert.IsNotNull(process);
            await StartupHealthMarker.WriteAsync(
                await fixture.ReadJournalAsync(),
                process!.ProcessId + 1,
                process.StartedAtUtc,
                runtime.UtcNow,
                CancellationToken.None);
        };
        var transaction = new PortableUpdateTransaction(
            runtime,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5));

        var result = await transaction.ExecuteAsync(fixture.Request, CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.RolledBack);
        Assert.AreEqual("current-application", await File.ReadAllTextAsync(fixture.InstalledApplication));
        Assert.AreEqual(UpdateTransactionState.RolledBack, (await fixture.ReadJournalAsync()).State);
    }

    [TestMethod]
    public async Task PortableRollbackPreservesDataWrittenByUpdatedVersion()
    {
        using var fixture = await PortableUpdateTestFixture.CreateAsync(portableDataMode: true);
        await fixture.WritePreparedJournalAsync();
        var runtime = new FakeUpdateTransactionRuntime(fixture.Now)
        {
            AdvancePerDelay = TimeSpan.FromSeconds(2),
        };
        runtime.OnDelayAsync = () => File.WriteAllTextAsync(
            Path.Combine(fixture.Request.InstallationDirectory, "data", "settings.json"),
            "data-written-by-target");
        var transaction = new PortableUpdateTransaction(
            runtime,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));

        var result = await transaction.ExecuteAsync(fixture.Request, CancellationToken.None);

        Assert.IsTrue(result.RolledBack);
        Assert.AreEqual("current-application", await File.ReadAllTextAsync(fixture.InstalledApplication));
        Assert.AreEqual(
            "data-written-by-target",
            await File.ReadAllTextAsync(Path.Combine(
                fixture.Request.InstallationDirectory,
                "data",
                "settings.json")));
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Request.InstallationDirectory, "portable.mode")));
    }

    [TestMethod]
    public async Task ParentIdentityMismatchDoesNotMutateInstallAndRestartsCurrentApplication()
    {
        using var fixture = await PortableUpdateTestFixture.CreateAsync();
        await fixture.WritePreparedJournalAsync();
        var runtime = new FakeUpdateTransactionRuntime(fixture.Now)
        {
            ParentResult = UpdateParentExitResult.IdentityMismatch,
        };
        var transaction = new PortableUpdateTransaction(runtime, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        var result = await transaction.ExecuteAsync(fixture.Request, CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.IsFalse(result.RolledBack);
        Assert.AreEqual("update.parent_identity_mismatch", result.SafeErrorCode);
        Assert.AreEqual("current-application", await File.ReadAllTextAsync(fixture.InstalledApplication));
        CollectionAssert.AreEqual(
            new[] { UpdateApplicationLaunchMode.Normal },
            runtime.LaunchModes.ToArray());
    }


    [TestMethod]
    public async Task ConsumedJournalRejectsReplayBeforeParentWaitOrFilesystemMutation()
    {
        using var fixture = await PortableUpdateTestFixture.CreateAsync();
        await fixture.WritePreparedJournalAsync();
        await fixture.AdvanceJournalAsync(UpdateTransactionState.WaitingForApplicationExit);
        var runtime = new FakeUpdateTransactionRuntime(fixture.Now);
        var transaction = new PortableUpdateTransaction(
            runtime,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            transaction.ExecuteAsync(fixture.Request, CancellationToken.None));

        Assert.AreEqual("current-application", await File.ReadAllTextAsync(fixture.InstalledApplication));
        Assert.AreEqual(0, runtime.ParentWaitCount);
        Assert.AreEqual(0, runtime.LaunchModes.Count);
    }

    private sealed class FakeUpdateTransactionRuntime(DateTimeOffset utcNow) : IUpdateTransactionRuntime
    {
        private int _nextProcessId = 5000;

        public DateTimeOffset UtcNow { get; private set; } = utcNow.ToUniversalTime();
        public UpdateParentExitResult ParentResult { get; set; } = UpdateParentExitResult.Exited;
        public TimeSpan AdvancePerDelay { get; set; } = TimeSpan.FromMilliseconds(250);
        public Func<Task>? OnDelayAsync { get; set; }
        public List<UpdateApplicationLaunchMode> LaunchModes { get; } = [];
        public FakeApplicationProcess? LastStartedProcess { get; private set; }
        public int ParentWaitCount { get; private set; }

        public Task<UpdateParentExitResult> WaitForParentExitAsync(
            int processId,
            DateTimeOffset expectedStartedAtUtc,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            ParentWaitCount++;
            return Task.FromResult(ParentResult);
        }

        public IUpdateApplicationProcess StartApplication(
            UpdateTransactionJournal journal,
            UpdateApplicationLaunchMode launchMode)
        {
            LaunchModes.Add(launchMode);
            LastStartedProcess = new FakeApplicationProcess(
                Interlocked.Increment(ref _nextProcessId),
                UtcNow.AddMilliseconds(10));
            return LastStartedProcess;
        }

        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (OnDelayAsync is { } action)
            {
                OnDelayAsync = null;
                await action();
            }

            UtcNow += AdvancePerDelay > delay ? AdvancePerDelay : delay;
        }
    }

    private sealed class FakeApplicationProcess(int processId, DateTimeOffset startedAtUtc) : IUpdateApplicationProcess
    {
        public int ProcessId { get; } = processId;
        public DateTimeOffset StartedAtUtc { get; } = startedAtUtc.ToUniversalTime();
        public bool HasExited { get; private set; }

        public void Terminate() => HasExited = true;

        public void Dispose()
        {
        }
    }
}
