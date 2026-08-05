using CodexUsageMonitor.Updater.Install;

namespace CodexUsageMonitor.IntegrationTests;

[TestClass]
public sealed class StartupHealthMarkerTests
{
    [TestMethod]
    public async Task MarkerBindsTransactionHashAndExactProcessIdentity()
    {
        using var fixture = await PortableUpdateTestFixture.CreateAsync();
        await fixture.WritePreparedJournalAsync();
        var journal = await fixture.AdvanceJournalAsync(
            UpdateTransactionState.WaitingForApplicationExit,
            UpdateTransactionState.BackedUp,
            UpdateTransactionState.Installed,
            UpdateTransactionState.Validating);
        var processStartedAt = fixture.Now.AddSeconds(1);
        await fixture.WriteHealthMarkerAsync(journal, 7001, processStartedAt);

        Assert.IsTrue(await StartupHealthMarker.IsValidAsync(
            journal,
            7001,
            processStartedAt,
            CancellationToken.None));
        Assert.IsFalse(await StartupHealthMarker.IsValidAsync(
            journal,
            7002,
            processStartedAt,
            CancellationToken.None));
        Assert.IsFalse(await StartupHealthMarker.IsValidAsync(
            journal,
            7001,
            processStartedAt.AddMilliseconds(1),
            CancellationToken.None));
    }

    [TestMethod]
    public async Task DirectoryAtMarkerPathIsRejectedWithoutDeletion()
    {
        using var fixture = await PortableUpdateTestFixture.CreateAsync();
        await fixture.WritePreparedJournalAsync();
        var journal = await fixture.AdvanceJournalAsync(
            UpdateTransactionState.WaitingForApplicationExit,
            UpdateTransactionState.BackedUp,
            UpdateTransactionState.Installed,
            UpdateTransactionState.Validating);
        Directory.CreateDirectory(journal.HealthMarkerPath);

        Assert.IsFalse(await StartupHealthMarker.IsValidAsync(
            journal,
            expectedProcessId: null,
            expectedProcessStartedAtUtc: null,
            CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            StartupHealthMarker.WriteAsync(
                journal,
                7001,
                fixture.Now.AddSeconds(1),
                fixture.Now.AddSeconds(2),
                CancellationToken.None));
        Assert.IsTrue(Directory.Exists(journal.HealthMarkerPath));
    }

    [TestMethod]
    public async Task OversizedOrDuplicatePropertyDocumentIsInvalid()
    {
        using var fixture = await PortableUpdateTestFixture.CreateAsync();
        await fixture.WritePreparedJournalAsync();
        var journal = await fixture.AdvanceJournalAsync(
            UpdateTransactionState.WaitingForApplicationExit,
            UpdateTransactionState.BackedUp,
            UpdateTransactionState.Installed,
            UpdateTransactionState.Validating);
        Directory.CreateDirectory(Path.GetDirectoryName(journal.HealthMarkerPath)!);
        await File.WriteAllTextAsync(
            journal.HealthMarkerPath,
            "{\"schemaVersion\":1,\"schemaVersion\":1}" + new string('x', StartupHealthMarker.MaximumSerializedBytes));

        Assert.IsFalse(await StartupHealthMarker.IsValidAsync(
            journal,
            expectedProcessId: null,
            expectedProcessStartedAtUtc: null,
            CancellationToken.None));
    }
}
