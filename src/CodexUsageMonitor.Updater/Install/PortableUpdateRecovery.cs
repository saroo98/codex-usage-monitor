namespace CodexUsageMonitor.Updater.Install;

public enum UpdateStartupOutcome
{
    Inspection,
    Healthy,
    Failed,
}

public enum UpdateRecoveryAction
{
    None,
    Active,
    Cleaned,
    Committed,
    RolledBack,
    RollbackRequired,
    Failed,
}

public sealed record UpdateRecoveryResult(
    Guid TransactionId,
    UpdateTransactionState? PreviousState,
    UpdateRecoveryAction Action,
    string? SafeErrorCode,
    UpdateTransactionJournal? Journal = null);

public sealed class PortableUpdateRecovery
{
    private const int MaximumTransactionsPerScan = 256;
    private readonly Func<DateTimeOffset> _utcNow;

    public PortableUpdateRecovery()
        : this(static () => DateTimeOffset.UtcNow)
    {
    }

    public PortableUpdateRecovery(Func<DateTimeOffset> utcNow)
    {
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    public async Task<IReadOnlyList<UpdateRecoveryResult>> ReconcileAsync(
        string installationDirectory,
        UpdateStartupOutcome startupOutcome,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(startupOutcome))
        {
            throw new ArgumentOutOfRangeException(nameof(startupOutcome));
        }

        var installation = UpdatePathLayout.NormalizeInstallationDirectory(installationDirectory);
        var transactionRoot = UpdatePathLayout.GetTransactionRoot(installation);
        if (!UpdatePathSecurity.PathEntryExists(transactionRoot))
        {
            return [];
        }

        UpdatePathSecurity.EnsureDirectoryTreeWithoutReparsePoints(
            transactionRoot,
            "The update transaction inventory is invalid.");
        var journalPaths = Directory.EnumerateFiles(transactionRoot, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumTransactionsPerScan + 1)
            .ToArray();
        if (journalPaths.Length > MaximumTransactionsPerScan)
        {
            return
            [
                new UpdateRecoveryResult(
                    Guid.Empty,
                    null,
                    UpdateRecoveryAction.Failed,
                    "update.recovery_inventory_limit"),
            ];
        }

        var inventory = new List<JournalInventoryItem>(journalPaths.Length);
        var results = new List<UpdateRecoveryResult>(journalPaths.Length);
        foreach (var journalPath in journalPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!UpdatePathLayout.TryParseTransactionId(journalPath, out var transactionId))
            {
                results.Add(new UpdateRecoveryResult(
                    Guid.Empty,
                    null,
                    UpdateRecoveryAction.Failed,
                    "update.recovery_invalid_journal_name"));
                continue;
            }

            try
            {
                var journal = await UpdateTransactionJournal.ReadAsync(
                    journalPath,
                    cancellationToken).ConfigureAwait(false);
                journal.ValidateForInstallation(installation);
                if (journal.TransactionId != transactionId)
                {
                    throw new InvalidDataException("Update transaction journal identity does not match its filename.");
                }

                inventory.Add(new JournalInventoryItem(journalPath, journal));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is
                IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                InvalidOperationException)
            {
                results.Add(new UpdateRecoveryResult(
                    transactionId,
                    null,
                    UpdateRecoveryAction.Failed,
                    "update.recovery_invalid_journal"));
            }
        }

        foreach (var item in inventory
                     .OrderByDescending(static item => item.Journal.UpdatedAtUtc)
                     .ThenByDescending(static item => item.Journal.TransactionId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var transactionLock = UpdateTransactionLock.TryAcquire(
                installation,
                item.Journal.TransactionId);
            if (transactionLock is null)
            {
                results.Add(new UpdateRecoveryResult(
                    item.Journal.TransactionId,
                    item.Journal.State,
                    UpdateRecoveryAction.Active,
                    null,
                    item.Journal));
                continue;
            }

            try
            {
                var journal = await UpdateTransactionJournal.ReadAsync(
                    item.JournalPath,
                    cancellationToken).ConfigureAwait(false);
                journal.ValidateForInstallation(installation);
                if (journal.TransactionId != item.Journal.TransactionId)
                {
                    throw new InvalidDataException("Update transaction journal identity changed during recovery.");
                }

                results.Add(await ReconcileJournalAsync(
                    journal,
                    item.JournalPath,
                    startupOutcome,
                    cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is
                IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                InvalidOperationException or
                System.Security.Cryptography.CryptographicException)
            {
                results.Add(new UpdateRecoveryResult(
                    item.Journal.TransactionId,
                    item.Journal.State,
                    UpdateRecoveryAction.Failed,
                    "update.recovery_failed",
                    item.Journal));
            }
        }

        return RejectConflictingRollbackCandidates(results);
    }

    private async Task<UpdateRecoveryResult> ReconcileJournalAsync(
        UpdateTransactionJournal journal,
        string journalPath,
        UpdateStartupOutcome startupOutcome,
        CancellationToken cancellationToken)
    {
        var previousState = journal.State;
        if (journal.State is UpdateTransactionState.Committed)
        {
            CleanupTerminal(journal);
            return Result(journal, previousState, UpdateRecoveryAction.Cleaned, null);
        }

        if (journal.State is UpdateTransactionState.RolledBack)
        {
            CleanupTerminal(journal);
            return Result(
                journal,
                previousState,
                UpdateRecoveryAction.Cleaned,
                journal.SafeErrorCode);
        }

        var evidence = await GatherEvidenceAsync(journal, cancellationToken).ConfigureAwait(false);

        // A power loss can occur after the installation was renamed to backup but before
        // the corresponding journal write. Repair only the exact one-way state that can
        // result from that move. Prepared state is never allowed to have mutated files.
        if (journal.State is UpdateTransactionState.WaitingForApplicationExit &&
            evidence.Backup is InstalledVersion.Previous &&
            evidence.Current is not InstalledVersion.Previous)
        {
            journal = await WriteStateAsync(
                journal,
                journalPath,
                UpdateTransactionState.BackedUp,
                null,
                cancellationToken).ConfigureAwait(false);
        }

        if (evidence.HealthConfirmed &&
            evidence.Current is InstalledVersion.Target &&
            journal.State is (UpdateTransactionState.BackedUp or
                UpdateTransactionState.Installed or
                UpdateTransactionState.Validating or
                UpdateTransactionState.Failed))
        {
            var committed = await WriteStateAsync(
                journal,
                journalPath,
                UpdateTransactionState.Committed,
                null,
                cancellationToken).ConfigureAwait(false);
            CleanupTerminal(committed);
            return Result(committed, previousState, UpdateRecoveryAction.Committed, null);
        }

        if (evidence.Current is InstalledVersion.Previous)
        {
            if (evidence.Backup is InstalledVersion.Missing &&
                evidence.FailedInstallation is InstalledVersion.Missing &&
                !evidence.DataCheckpointExists)
            {
                if (journal.State is UpdateTransactionState.Prepared or
                    UpdateTransactionState.WaitingForApplicationExit)
                {
                    return await MarkFailedBeforeMutationAsync(
                        journal,
                        journalPath,
                        previousState,
                        cancellationToken).ConfigureAwait(false);
                }

                if (journal.State is UpdateTransactionState.Failed)
                {
                    CleanupFailedBeforeMutation(journal);
                    return Result(
                        journal,
                        previousState,
                        UpdateRecoveryAction.Failed,
                        journal.SafeErrorCode ?? "update.interrupted_before_install");
                }
            }

            if (evidence.Backup is not InstalledVersion.Missing)
            {
                return await MarkFailedAsync(
                    journal,
                    journalPath,
                    previousState,
                    evidence.Backup is InstalledVersion.Previous
                        ? "update.recovery_duplicate_prior_version"
                        : "update.rollback_backup_invalid",
                    cancellationToken).ConfigureAwait(false);
            }

            var rolledBack = await AdvanceToRolledBackAsync(
                journal,
                journalPath,
                journal.SafeErrorCode ?? "update.interrupted_transaction",
                cancellationToken).ConfigureAwait(false);
            CleanupTerminal(rolledBack);
            return Result(
                rolledBack,
                previousState,
                UpdateRecoveryAction.RolledBack,
                rolledBack.SafeErrorCode);
        }

        if (journal.State is UpdateTransactionState.Prepared or
            UpdateTransactionState.WaitingForApplicationExit)
        {
            if (evidence.Backup is InstalledVersion.Missing &&
                evidence.FailedInstallation is InstalledVersion.Missing &&
                !evidence.DataCheckpointExists)
            {
                return await MarkFailedBeforeMutationAsync(
                    journal,
                    journalPath,
                    previousState,
                    cancellationToken).ConfigureAwait(false);
            }

            return await MarkFailedAsync(
                journal,
                journalPath,
                previousState,
                "update.recovery_preinstall_state_inconsistent",
                cancellationToken).ConfigureAwait(false);
        }

        if (evidence.Backup is InstalledVersion.Previous)
        {
            if (evidence.Current is InstalledVersion.Target)
            {
                if (startupOutcome is UpdateStartupOutcome.Inspection &&
                    journal.State is not (UpdateTransactionState.RollingBack or UpdateTransactionState.Failed))
                {
                    return Result(journal, previousState, UpdateRecoveryAction.None, null);
                }

                return RollbackRequired(
                    journal,
                    previousState,
                    startupOutcome is UpdateStartupOutcome.Healthy
                        ? evidence.HealthMarkerExists
                            ? "update.startup_health_invalid"
                            : "update.startup_health_missing"
                        : journal.SafeErrorCode ?? "update.interrupted_transaction");
            }

            // Missing or unknown current payload is never guessed. Preserve any failed
            // payload and let the external host resume the verified rollback state machine.
            return RollbackRequired(
                journal,
                previousState,
                journal.SafeErrorCode ?? "update.recovery_current_version_unknown");
        }

        return await MarkFailedAsync(
            journal,
            journalPath,
            previousState,
            evidence.Backup is InstalledVersion.Unknown
                ? "update.rollback_backup_invalid"
                : "update.rollback_backup_missing",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<RecoveryEvidence> GatherEvidenceAsync(
        UpdateTransactionJournal journal,
        CancellationToken cancellationToken)
    {
        var current = await ClassifyInstallationAsync(
            journal.InstallationDirectory,
            journal,
            cancellationToken).ConfigureAwait(false);
        var backup = await ClassifyInstallationAsync(
            journal.BackupDirectory,
            journal,
            cancellationToken).ConfigureAwait(false);
        var failedInstallation = await ClassifyInstallationAsync(
            UpdatePathLayout.GetFailedInstallationDirectory(
                journal.InstallationDirectory,
                journal.TransactionId),
            journal,
            cancellationToken).ConfigureAwait(false);

        var checkpointPath = UpdatePathLayout.GetRollbackDataCheckpointDirectory(
            journal.InstallationDirectory,
            journal.TransactionId);
        var dataCheckpointExists = UpdatePathSecurity.PathEntryExists(checkpointPath);
        if (dataCheckpointExists)
        {
            UpdatePathSecurity.EnsureDirectoryTreeWithoutReparsePoints(
                checkpointPath,
                "The rollback data checkpoint is occupied by an invalid filesystem entry.");
        }

        var healthMarkerExists = UpdatePathSecurity.PathEntryExists(journal.HealthMarkerPath);
        var healthConfirmed = healthMarkerExists &&
            await StartupHealthMarker.IsValidAsync(
                journal,
                expectedProcessId: null,
                expectedProcessStartedAtUtc: null,
                cancellationToken).ConfigureAwait(false);
        return new RecoveryEvidence(
            current,
            backup,
            failedInstallation,
            dataCheckpointExists,
            healthMarkerExists,
            healthConfirmed);
    }

    private static async Task<InstalledVersion> ClassifyInstallationAsync(
        string installationDirectory,
        UpdateTransactionJournal journal,
        CancellationToken cancellationToken)
    {
        if (!UpdatePathSecurity.PathEntryExists(installationDirectory))
        {
            return InstalledVersion.Missing;
        }

        try
        {
            UpdatePathSecurity.EnsureDirectoryTreeWithoutReparsePoints(
                installationDirectory,
                "The update installation is unsafe during recovery.");
            var executable = Path.Combine(
                installationDirectory,
                journal.ApplicationExecutableName);
            var currentHash = await UpdateFileIntegrity.ComputeSha256Async(
                executable,
                cancellationToken).ConfigureAwait(false);
            var markerPath = Path.Combine(
                installationDirectory,
                PortableUpdateData.MarkerFileName);
            var markerExists = UpdatePathSecurity.PathEntryExists(markerPath);
            if (markerExists)
            {
                UpdatePathSecurity.EnsureRegularFile(
                    markerPath,
                    "The portable-mode marker is invalid during update recovery.");
            }

            if (markerExists != journal.PortableDataMode)
            {
                return InstalledVersion.Unknown;
            }

            if (UpdateFileIntegrity.FixedTimeEquals(
                    journal.ExpectedCurrentApplicationSha256,
                    currentHash))
            {
                return InstalledVersion.Previous;
            }

            return UpdateFileIntegrity.FixedTimeEquals(
                    journal.TargetApplicationSha256,
                    currentHash)
                ? InstalledVersion.Target
                : InstalledVersion.Unknown;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            InvalidDataException)
        {
            return InstalledVersion.Unknown;
        }
    }

    private async Task<UpdateRecoveryResult> MarkFailedBeforeMutationAsync(
        UpdateTransactionJournal journal,
        string journalPath,
        UpdateTransactionState previousState,
        CancellationToken cancellationToken)
    {
        var result = await MarkFailedAsync(
            journal,
            journalPath,
            previousState,
            "update.interrupted_before_install",
            cancellationToken).ConfigureAwait(false);
        CleanupFailedBeforeMutation(result.Journal ?? journal);
        return result;
    }

    private async Task<UpdateRecoveryResult> MarkFailedAsync(
        UpdateTransactionJournal journal,
        string journalPath,
        UpdateTransactionState previousState,
        string safeErrorCode,
        CancellationToken cancellationToken)
    {
        var failed = journal.WithState(
            UpdateTransactionState.Failed,
            _utcNow(),
            safeErrorCode);
        await failed.WriteAsync(journalPath, cancellationToken).ConfigureAwait(false);
        return Result(
            failed,
            previousState,
            UpdateRecoveryAction.Failed,
            safeErrorCode);
    }

    private async Task<UpdateTransactionJournal> AdvanceToRolledBackAsync(
        UpdateTransactionJournal journal,
        string journalPath,
        string safeErrorCode,
        CancellationToken cancellationToken)
    {
        if (journal.State is UpdateTransactionState.WaitingForApplicationExit)
        {
            journal = await WriteStateAsync(
                journal,
                journalPath,
                UpdateTransactionState.BackedUp,
                null,
                cancellationToken).ConfigureAwait(false);
        }

        if (journal.State is UpdateTransactionState.Prepared)
        {
            journal = await WriteStateAsync(
                journal,
                journalPath,
                UpdateTransactionState.Failed,
                safeErrorCode,
                cancellationToken).ConfigureAwait(false);
        }

        return await WriteStateAsync(
            journal,
            journalPath,
            UpdateTransactionState.RolledBack,
            safeErrorCode,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<UpdateTransactionJournal> WriteStateAsync(
        UpdateTransactionJournal journal,
        string journalPath,
        UpdateTransactionState state,
        string? safeErrorCode,
        CancellationToken cancellationToken)
    {
        var next = journal.WithState(state, _utcNow(), safeErrorCode);
        await next.WriteAsync(journalPath, cancellationToken).ConfigureAwait(false);
        return next;
    }

    private static UpdateRecoveryResult RollbackRequired(
        UpdateTransactionJournal journal,
        UpdateTransactionState previousState,
        string safeErrorCode) =>
        Result(
            journal,
            previousState,
            UpdateRecoveryAction.RollbackRequired,
            safeErrorCode);

    private static UpdateRecoveryResult Result(
        UpdateTransactionJournal journal,
        UpdateTransactionState previousState,
        UpdateRecoveryAction action,
        string? safeErrorCode) =>
        new(
            journal.TransactionId,
            previousState,
            action,
            safeErrorCode,
            journal);

    private static IReadOnlyList<UpdateRecoveryResult> RejectConflictingRollbackCandidates(
        List<UpdateRecoveryResult> results)
    {
        var candidateIds = results
            .Where(static result => result.Action is UpdateRecoveryAction.RollbackRequired)
            .Select(static result => result.TransactionId)
            .Where(static transactionId => transactionId != Guid.Empty)
            .Distinct()
            .ToArray();
        if (candidateIds.Length <= 1)
        {
            return results;
        }

        var conflicts = candidateIds.ToHashSet();
        return results
            .Select(result => conflicts.Contains(result.TransactionId) &&
                    result.Action is UpdateRecoveryAction.RollbackRequired
                ? result with
                {
                    Action = UpdateRecoveryAction.Failed,
                    SafeErrorCode = "update.recovery_conflicting_transactions",
                }
                : result)
            .ToArray();
    }

    private static void CleanupTerminal(UpdateTransactionJournal journal)
    {
        TryDeleteDirectory(journal.BackupDirectory);
        TryDeleteDirectory(journal.StagingDirectory);
        TryDeleteDirectory(UpdatePathLayout.GetFailedInstallationDirectory(
            journal.InstallationDirectory,
            journal.TransactionId));
        TryDeleteDirectory(UpdatePathLayout.GetRollbackDataCheckpointDirectory(
            journal.InstallationDirectory,
            journal.TransactionId));
        TryDeleteFile(journal.HealthMarkerPath);
        TryDeleteDirectory(Path.GetDirectoryName(journal.UpdaterHostPath)!);
    }

    private static void CleanupFailedBeforeMutation(UpdateTransactionJournal journal)
    {
        PortableUpdateData.RemovePreparedStagedPayload(journal.StagingDirectory);
        TryDeleteDirectory(journal.StagingDirectory);
        TryDeleteFile(journal.HealthMarkerPath);
        TryDeleteDirectory(Path.GetDirectoryName(journal.UpdaterHostPath)!);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (UpdatePathSecurity.PathEntryExists(path))
            {
                UpdatePathSecurity.EnsureRegularFile(
                    path,
                    "The update recovery artifact is unsafe to delete.");
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            InvalidDataException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (UpdatePathSecurity.PathEntryExists(path))
            {
                UpdatePathSecurity.DeleteDirectoryTree(
                    path,
                    "The update recovery directory is unsafe to delete.");
            }
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            InvalidDataException)
        {
        }
    }

    private enum InstalledVersion
    {
        Missing,
        Previous,
        Target,
        Unknown,
    }

    private sealed record JournalInventoryItem(
        string JournalPath,
        UpdateTransactionJournal Journal);

    private sealed record RecoveryEvidence(
        InstalledVersion Current,
        InstalledVersion Backup,
        InstalledVersion FailedInstallation,
        bool DataCheckpointExists,
        bool HealthMarkerExists,
        bool HealthConfirmed);
}
