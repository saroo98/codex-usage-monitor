using CodexUsageMonitor.Application.Updates;

namespace CodexUsageMonitor.UnitTests;

[TestClass]
public sealed class UpdateRecoveryPolicyTests
{
    [TestMethod]
    public void SelectSingleRejectsConflictingTransactions()
    {
        var result = UpdateRecoveryPolicy.SelectSingle(
        [
            new UpdateRecoveryCandidate(Guid.NewGuid(), DateTimeOffset.UtcNow),
            new UpdateRecoveryCandidate(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-1)),
        ]);

        Assert.AreEqual(UpdateRecoverySelectionStatus.Conflict, result.Status);
        Assert.IsNull(result.TransactionId);
    }

    [TestMethod]
    public void SelectSingleReturnsOnlyTransaction()
    {
        var transactionId = Guid.NewGuid();

        var result = UpdateRecoveryPolicy.SelectSingle(
            [new UpdateRecoveryCandidate(transactionId, DateTimeOffset.UtcNow)]);

        Assert.AreEqual(UpdateRecoverySelectionStatus.Selected, result.Status);
        Assert.AreEqual(transactionId, result.TransactionId);
    }
}
