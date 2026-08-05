using System.Security.Cryptography;
using CodexUsageMonitor.Application.Migration;
using CodexUsageMonitor.Migration.Execution;

namespace CodexUsageMonitor.App.Services;

public sealed class LegacyMigrationStateAdapter(
    LegacyMigrationRuntimeState runtime,
    ILegacyTaskRetirementCoordinator retirement) : ILegacyMigrationStatePort
{
    private readonly LegacyMigrationRuntimeState _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    private readonly ILegacyTaskRetirementCoordinator _retirement = retirement ?? throw new ArgumentNullException(nameof(retirement));

    public LegacyMigrationStateSnapshot? Migration => Map(_runtime.Migration);

    public LegacyTaskRetirementSnapshot? Retirement => Map(_runtime.Retirement);

    public async Task<LegacyTaskRetirementSnapshot?> ReadRetirementAsync(CancellationToken cancellationToken)
    {
        var state = await _retirement.GetStateAsync(cancellationToken).ConfigureAwait(false);
        if (state is not null)
        {
            _runtime.SetRetirement(state);
        }

        return Map(state);
    }

    public async Task<LegacyTaskRetirementSnapshot> RetireAsync(CancellationToken cancellationToken)
    {
        var state = await _retirement.RetireAsync(explicitlyConfirmed: true, cancellationToken).ConfigureAwait(false);
        _runtime.SetRetirement(state);
        return Map(state)!;
    }

    public async Task<LegacyTaskRetirementSnapshot> RestoreAsync(CancellationToken cancellationToken)
    {
        var state = await _retirement.RestoreAsync(explicitlyConfirmed: true, cancellationToken).ConfigureAwait(false);
        _runtime.SetRetirement(state);
        return Map(state)!;
    }

    public void SetRetirement(LegacyTaskRetirementSnapshot? state)
    {
        // The adapter persists the concrete migration state while performing each read or mutation.
    }

    private static LegacyMigrationStateSnapshot? Map(LegacyMigrationResult? result) => result is null
        ? null
        : new LegacyMigrationStateSnapshot(
            result.MigrationFound,
            result.Migrated,
            result.LegacyVersion,
            result.BackupArchive,
            result.BackupArchiveSha256,
            result.Warnings,
            result.SafeErrorCode);

    private static LegacyTaskRetirementSnapshot? Map(LegacyTaskRetirementState? state) => state is null
        ? null
        : new LegacyTaskRetirementSnapshot(state.IsRetired, state.HasFailures);
}

public sealed class LegacyBackupVerificationAdapter : ILegacyBackupVerificationPort
{
    private const long MaximumBackupBytes = 512L * 1024 * 1024;

    public async Task<bool> VerifyAsync(
        string? archivePath,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || string.IsNullOrWhiteSpace(expectedSha256) || !File.Exists(archivePath))
        {
            return false;
        }

        var info = new FileInfo(archivePath);
        if (info.Length <= 0 || info.Length > MaximumBackupBytes)
        {
            return false;
        }

        await using var stream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            8192,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = Convert.ToHexStringLower(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(actual),
            System.Text.Encoding.ASCII.GetBytes(expectedSha256.ToLowerInvariant()));
    }
}
