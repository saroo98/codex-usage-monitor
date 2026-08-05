using CodexUsageMonitor.Application.Diagnostics;
using CodexUsageMonitor.Application.Monitoring;
using CodexUsageMonitor.Core.Abstractions;
using CodexUsageMonitor.Core.Monitoring;

namespace CodexUsageMonitor.Application.Migration;

public sealed record LegacyMigrationSummary(
    bool LegacyInstallationDetected,
    bool SettingsImported,
    string? LegacyVersion,
    string? BackupArchive,
    bool BackupVerified,
    bool TasksRetired,
    bool CanRetireTasks,
    bool CanRestoreTasks,
    string SafeStatusCode);

public sealed record LegacyMigrationOperationResult(
    bool Succeeded,
    string SafeStatusCode,
    LegacyMigrationSummary Summary);

public sealed class LegacyMigrationActionService
{
    private static readonly TimeSpan FreshnessLimit = TimeSpan.FromMinutes(2);
    private readonly ILegacyMigrationStatePort _migration;
    private readonly ILegacyBackupVerificationPort _backup;
    private readonly IUsageRuntimeSnapshotProvider _usage;
    private readonly IClock _clock;
    private readonly IApplicationFailureSink _failures;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LegacyMigrationActionService(
        ILegacyMigrationStatePort migration,
        ILegacyBackupVerificationPort backup,
        IUsageRuntimeSnapshotProvider usage,
        IClock clock,
        IApplicationFailureSink failures)
    {
        _migration = migration ?? throw new ArgumentNullException(nameof(migration));
        _backup = backup ?? throw new ArgumentNullException(nameof(backup));
        _usage = usage ?? throw new ArgumentNullException(nameof(usage));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _failures = failures ?? throw new ArgumentNullException(nameof(failures));
    }

    public async Task<LegacyMigrationSummary> GetSummaryAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await GetSummaryCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<LegacyMigrationOperationResult> RetireAsync(bool explicitlyConfirmed, CancellationToken cancellationToken) =>
        ChangeRetirementAsync(explicitlyConfirmed, restore: false, cancellationToken);

    public Task<LegacyMigrationOperationResult> RestoreAsync(bool explicitlyConfirmed, CancellationToken cancellationToken) =>
        ChangeRetirementAsync(explicitlyConfirmed, restore: true, cancellationToken);

    private async Task<LegacyMigrationOperationResult> ChangeRetirementAsync(
        bool explicitlyConfirmed,
        bool restore,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var summary = await GetSummaryCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!explicitlyConfirmed)
            {
                return new LegacyMigrationOperationResult(false, "migration.confirmation_required", summary);
            }

            if (restore ? !summary.CanRestoreTasks : !summary.CanRetireTasks)
            {
                var blocked = restore ? "migration.tasks_not_retired" : DetermineRetirementBlock(summary);
                return new LegacyMigrationOperationResult(false, blocked, summary);
            }

            var state = restore
                ? await _migration.RestoreAsync(cancellationToken).ConfigureAwait(false)
                : await _migration.RetireAsync(cancellationToken).ConfigureAwait(false);
            _migration.SetRetirement(state);
            var updated = await GetSummaryCoreAsync(cancellationToken).ConfigureAwait(false);
            var succeeded = restore ? !state.IsRetired && !state.HasFailures : state.IsRetired && !state.HasFailures;
            return new LegacyMigrationOperationResult(
                succeeded,
                succeeded
                    ? restore ? "migration.tasks_restored" : "migration.tasks_retired"
                    : restore ? "migration.task_restore_partial" : "migration.task_retirement_partial",
                updated);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            var safeCode = restore ? "migration.task_restore_failed" : "migration.task_retirement_failed";
            _failures.Report(safeCode, exception);
            var summary = await GetSummaryCoreAsync(cancellationToken).ConfigureAwait(false);
            return new LegacyMigrationOperationResult(false, safeCode, summary);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<LegacyMigrationSummary> GetSummaryCoreAsync(CancellationToken cancellationToken)
    {
        var migration = _migration.Migration;
        var retirement = _migration.Retirement ?? await _migration.ReadRetirementAsync(cancellationToken).ConfigureAwait(false);
        if (_migration.Retirement is null && retirement is not null)
        {
            _migration.SetRetirement(retirement);
        }

        var backupVerified = await _backup.VerifyAsync(
            migration?.BackupArchive,
            migration?.BackupArchiveSha256,
            cancellationToken).ConfigureAwait(false);
        var fresh = HasFreshLiveSnapshot();
        var detected = migration?.MigrationFound == true;
        var imported = migration is { SafeErrorCode: null } && detected &&
            (migration.Migrated || migration.Warnings.Contains("migration.already_completed", StringComparer.Ordinal));
        var retired = retirement?.IsRetired == true;
        var canRetire = imported && backupVerified && !retired && fresh;
        return new LegacyMigrationSummary(
            detected,
            imported,
            migration?.LegacyVersion,
            migration?.BackupArchive,
            backupVerified,
            retired,
            canRetire,
            retired,
            migration switch
            {
                null => "migration.status_unavailable",
                { MigrationFound: false } => "migration.not_found",
                { SafeErrorCode: not null } => migration.SafeErrorCode,
                _ when retired => "migration.tasks_retired",
                _ when !backupVerified => "migration.backup_unverified",
                _ when !fresh => "migration.awaiting_fresh_snapshot",
                _ => "migration.ready_to_retire",
            });
    }

    private bool HasFreshLiveSnapshot()
    {
        var snapshot = _usage.ActiveSnapshot;
        var monitor = _usage.ActiveMonitorState;
        if (snapshot is null || monitor.Connection is not (MonitorConnectionState.Live or MonitorConnectionState.Delayed))
        {
            return false;
        }

        var now = _clock.UtcNow;
        var observedAge = now - snapshot.ObservedAtUtc;
        var successAge = monitor.LastSuccessAtUtc is { } success ? now - success : TimeSpan.MaxValue;
        return observedAge >= TimeSpan.Zero && observedAge <= FreshnessLimit &&
            successAge >= TimeSpan.Zero && successAge <= FreshnessLimit;
    }

    private static string DetermineRetirementBlock(LegacyMigrationSummary summary) => summary switch
    {
        { LegacyInstallationDetected: false } => "migration.not_found",
        { SettingsImported: false } => "migration.not_imported",
        { BackupVerified: false } => "migration.backup_unverified",
        { TasksRetired: true } => "migration.tasks_already_retired",
        _ => "migration.awaiting_fresh_snapshot",
    };
}
