namespace CodexUsageMonitor.Application.Updates;

public sealed record UpdateRecoveryCandidate(Guid TransactionId, DateTimeOffset UpdatedAtUtc);

public enum UpdateRecoverySelectionStatus
{
    None,
    Selected,
    Conflict,
}

public sealed record UpdateRecoverySelection(
    UpdateRecoverySelectionStatus Status,
    Guid? TransactionId = null);

public static class UpdateRecoveryPolicy
{
    public static UpdateRecoverySelection SelectSingle(IReadOnlyCollection<UpdateRecoveryCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates.Count switch
        {
            0 => new UpdateRecoverySelection(UpdateRecoverySelectionStatus.None),
            1 => new UpdateRecoverySelection(UpdateRecoverySelectionStatus.Selected, candidates.Single().TransactionId),
            _ => new UpdateRecoverySelection(UpdateRecoverySelectionStatus.Conflict),
        };
    }
}
