namespace CodexUsageMonitor.Application.Migration;

public sealed record LegacyMigrationStateSnapshot(
    bool MigrationFound,
    bool Migrated,
    string? LegacyVersion,
    string? BackupArchive,
    string? BackupArchiveSha256,
    IReadOnlyList<string> Warnings,
    string? SafeErrorCode);

public sealed record LegacyTaskRetirementSnapshot(bool IsRetired, bool HasFailures);

public interface ILegacyMigrationStatePort
{
    LegacyMigrationStateSnapshot? Migration { get; }

    LegacyTaskRetirementSnapshot? Retirement { get; }

    Task<LegacyTaskRetirementSnapshot?> ReadRetirementAsync(CancellationToken cancellationToken);

    Task<LegacyTaskRetirementSnapshot> RetireAsync(CancellationToken cancellationToken);

    Task<LegacyTaskRetirementSnapshot> RestoreAsync(CancellationToken cancellationToken);

    void SetRetirement(LegacyTaskRetirementSnapshot? state);
}

public interface ILegacyBackupVerificationPort
{
    Task<bool> VerifyAsync(string? archivePath, string? expectedSha256, CancellationToken cancellationToken);
}
