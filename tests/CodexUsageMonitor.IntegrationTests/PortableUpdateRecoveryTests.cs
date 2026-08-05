using CodexUsageMonitor.Updater.Install;

namespace CodexUsageMonitor.IntegrationTests;

[TestClass]
public sealed class PortableUpdateRecoveryTests
{
    [TestMethod]
    public async Task HealthyTargetWithMarkerCommitsInterruptedTransaction()
    {
        using var fixture = await PortableUpdateTestFixture.CreateAsync();
        await fixture.WritePreparedJournalAsync();
        fixture.InstallStagedPayload();
        var journal = await fixture.AdvanceJournalAsync(
            UpdateTransactionState.WaitingForApplicationExit,
            UpdateTransactionState.BackedUp,
            UpdateTransactionState.Installed,
            UpdateTransactionState.Validating);
        await fixture.WriteHealthMarkerAsync(journal);

        var results = await new PortableUpdateRecovery(() => fixture.Now.AddMinutes(1)).ReconcileAsync(
            fixture.Request.InstallationDirectory,
            UpdateStartupOutcome.Healthy,
            CancellationToken.None);

        var result = AssertSingle(results);
        Assert.AreEqual(UpdateRecoveryAction.Committed, result.Action);
        Assert.AreEqual(UpdateTransactionState.Committed, (await fixture.ReadJournalAsync()).State);
        Assert.IsFalse(Directory.Exists(fixture.Request.BackupDirectory));
    }

    [TestMethod]
    public async Task FailedStartupWithBackupRequestsRollbackInsteadOfGuessingCurrentVersion()
    {
        using var fixture = await PortableUpdateTestFixture.CreateAsync();
        await fixture.WritePreparedJournalAsync();
        fixture.InstallStagedPayload();
        await File.WriteAllTextAsync(fixture.InstalledApplication, "unknown-build");
        await fixture.AdvanceJournalAsync(
            UpdateTransactionState.WaitingForApplicationExit,
            UpdateTransactionState.BackedUp,
            UpdateTransactionState.Installed,
            UpdateTransactionState.Validating);

        var results = await new PortableUpdateRecovery(() => fixture.Now.AddMinutes(1)).ReconcileAsync(
            fixture.Request.InstallationDirectory,
            UpdateStartupOutcome.Failed,
            CancellationToken.None);

        var result = AssertSingle(results);
        Assert.AreEqual(UpdateRecoveryAction.RollbackRequired, result.Action);
        Assert.AreEqual("update.recovery_current_version_unknown", result.SafeErrorCode);
        Assert.IsNotNull(result.Journal);
    }

    [TestMethod]
    public async Task InvalidHealthMarkerNeverCommitsTargetPayload()
    {
        using var fixture = await PortableUpdateTestFixture.CreateAsync();
        await fixture.WritePreparedJournalAsync();
        fixture.InstallStagedPayload();
        await fixture.AdvanceJournalAsync(
            UpdateTransactionState.WaitingForApplicationExit,
            UpdateTransactionState.BackedUp,
            UpdateTransactionState.Installed,
            UpdateTransactionState.Validating);
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Request.HealthMarkerPath)!);
        await File.WriteAllTextAsync(fixture.Request.HealthMarkerPath, "not-a-valid-health-document");

        var results = await new PortableUpdateRecovery(() => fixture.Now.AddMinutes(1)).ReconcileAsync(
            fixture.Request.InstallationDirectory,
            UpdateStartupOutcome.Healthy,
            CancellationToken.None);

        var result = AssertSingle(results);
        Assert.AreEqual(UpdateRecoveryAction.RollbackRequired, result.Action);
        Assert.AreEqual("update.startup_health_invalid", result.SafeErrorCode);
        Assert.AreEqual(UpdateTransactionState.Validating, (await fixture.ReadJournalAsync()).State);
    }

    [TestMethod]
    public async Task PreviousVersionAlreadyRestoredMarksTransactionRolledBack()
    {
        using var fixture = await PortableUpdateTestFixture.CreateAsync();
        await fixture.WritePreparedJournalAsync();
        await fixture.AdvanceJournalAsync(
            UpdateTransactionState.WaitingForApplicationExit,
            UpdateTransactionState.BackedUp,
            UpdateTransactionState.Installed,
            UpdateTransactionState.Validating,
            UpdateTransactionState.RollingBack);

        var results = await new PortableUpdateRecovery(() => fixture.Now.AddMinutes(1)).ReconcileAsync(
            fixture.Request.InstallationDirectory,
            UpdateStartupOutcome.Healthy,
            CancellationToken.None);

        var result = AssertSingle(results);
        Assert.AreEqual(UpdateRecoveryAction.RolledBack, result.Action);
        Assert.AreEqual(UpdateTransactionState.RolledBack, (await fixture.ReadJournalAsync()).State);
    }

    private static UpdateRecoveryResult AssertSingle(IReadOnlyList<UpdateRecoveryResult> results)
    {
        Assert.AreEqual(1, results.Count);
        return results[0];
    }
}
