using CodexUsageMonitor.Updater.Install;

namespace CodexUsageMonitor.IntegrationTests;

[TestClass]
public sealed class UpdateRollbackRequestTests
{
    [TestMethod]
    public async Task CanonicalEnvelopeRoundTripsAndBindsTrustedHostDirectory()
    {
        using var fixture = await PortableUpdateTestFixture.CreateAsync();
        await fixture.WritePreparedJournalAsync();
        var journal = await fixture.AdvanceJournalAsync(
            UpdateTransactionState.WaitingForApplicationExit,
            UpdateTransactionState.BackedUp,
            UpdateTransactionState.Installed,
            UpdateTransactionState.Validating);
        var request = UpdateRollbackRequest.Create(
            journal,
            8123,
            fixture.Now.AddSeconds(-1),
            fixture.Now);
        var requestPath = UpdatePathLayout.GetRollbackRequestPath(
            fixture.Request.InstallationDirectory,
            fixture.Request.TransactionId);

        await request.WriteAsync(requestPath, CancellationToken.None);
        var roundTrip = await UpdateRollbackRequest.ReadAsync(requestPath, CancellationToken.None);
        roundTrip.ValidateEnvelope(
            request.Nonce,
            fixture.Request.UpdaterHostPath,
            requestPath,
            fixture.Now.AddSeconds(1));

        Assert.AreEqual(request, roundTrip);
    }

    [TestMethod]
    public async Task RejectsWrongNonceExpiredEnvelopeAndPathReplay()
    {
        using var fixture = await PortableUpdateTestFixture.CreateAsync();
        await fixture.WritePreparedJournalAsync();
        var journal = await fixture.AdvanceJournalAsync(
            UpdateTransactionState.WaitingForApplicationExit,
            UpdateTransactionState.BackedUp,
            UpdateTransactionState.Installed,
            UpdateTransactionState.Validating);
        var request = UpdateRollbackRequest.Create(
            journal,
            8123,
            fixture.Now.AddSeconds(-1),
            fixture.Now);
        var requestPath = UpdatePathLayout.GetRollbackRequestPath(
            fixture.Request.InstallationDirectory,
            fixture.Request.TransactionId);
        await request.WriteAsync(requestPath, CancellationToken.None);

        Assert.ThrowsExactly<InvalidDataException>(() => request.ValidateEnvelope(
            new string('0', 64),
            fixture.Request.UpdaterHostPath,
            requestPath,
            fixture.Now.AddSeconds(1)));
        Assert.ThrowsExactly<InvalidDataException>(() => request.ValidateEnvelope(
            request.Nonce,
            fixture.Request.UpdaterHostPath,
            requestPath,
            fixture.Now.AddMinutes(11)));
        Assert.ThrowsExactly<InvalidDataException>(() => request.ValidateEnvelope(
            request.Nonce,
            fixture.Request.UpdaterHostPath,
            fixture.JournalPath,
            fixture.Now.AddSeconds(1)));
    }

    [TestMethod]
    public async Task ReadRejectsOversizedAndDuplicatePropertyDocuments()
    {
        using var fixture = await PortableUpdateTestFixture.CreateAsync();
        var path = Path.Combine(fixture.Root, "invalid-rollback.json");
        await File.WriteAllTextAsync(
            path,
            "{\"schemaVersion\":2,\"schemaVersion\":2,\"padding\":\"" +
            new string('x', UpdateRollbackRequest.MaximumSerializedBytes) +
            "\"}");

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UpdateRollbackRequest.ReadAsync(path, CancellationToken.None));
    }
}
