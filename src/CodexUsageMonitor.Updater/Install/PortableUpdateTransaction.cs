using CodexUsageMonitor.Updater.Staging;

namespace CodexUsageMonitor.Updater.Install;

public sealed record UpdateTransactionResult(bool Succeeded, bool RolledBack, string? SafeErrorCode);

public sealed class PortableUpdateTransaction
{
    private static readonly TimeSpan DefaultParentExitTimeout = TimeSpan.FromSeconds(45);
    // Startup health qualification can consume up to 30 seconds after a cold start.
    // Allow additional bounded time for process launch, endpoint security scanning, and first-run initialization.
    private static readonly TimeSpan DefaultStartupHealthTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan HealthPollInterval = TimeSpan.FromMilliseconds(250);
    private readonly IUpdateTransactionRuntime _runtime;
    private readonly TimeSpan _parentExitTimeout;
    private readonly TimeSpan _startupHealthTimeout;

    public PortableUpdateTransaction()
        : this(new SystemUpdateTransactionRuntime(), DefaultParentExitTimeout, DefaultStartupHealthTimeout)
    {
    }

    public PortableUpdateTransaction(
        IUpdateTransactionRuntime runtime,
        TimeSpan parentExitTimeout,
        TimeSpan startupHealthTimeout)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(parentExitTimeout, TimeSpan.Zero);

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(startupHealthTimeout, TimeSpan.Zero);

        _parentExitTimeout = parentExitTimeout;
        _startupHealthTimeout = startupHealthTimeout;
    }

    public async Task<UpdateTransactionResult> ExecuteAsync(
        UpdateInstallRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var installation = UpdatePathLayout.NormalizeInstallationDirectory(request.InstallationDirectory);
        var journalPath = UpdatePathLayout.GetTransactionJournalPath(installation, request.TransactionId);
        using var transactionLock = UpdateTransactionLock.Acquire(installation, request.TransactionId);
        var journal = await UpdateTransactionJournal.ReadAsync(journalPath, cancellationToken).ConfigureAwait(false);
        journal.ValidateForInstallation(installation);
        request.ValidateAgainst(journal);
        if (journal.State is not UpdateTransactionState.Prepared)
        {
            throw new InvalidDataException("The update transaction has already been consumed.");
        }

        await request.VerifyPayloadAsync(cancellationToken).ConfigureAwait(false);
        journal = await WriteStateAsync(
            journal,
            journalPath,
            UpdateTransactionState.WaitingForApplicationExit,
            null,
            cancellationToken).ConfigureAwait(false);
        var parentResult = await _runtime.WaitForParentExitAsync(
            request.ParentProcessId,
            request.ParentProcessStartedAtUtc,
            _parentExitTimeout,
            cancellationToken).ConfigureAwait(false);
        if (parentResult is not UpdateParentExitResult.Exited)
        {
            return await HandleParentExitFailureAsync(
                journal,
                journalPath,
                parentResult,
                restartWhenIdentityMismatch: true).ConfigureAwait(false);
        }

        var stagedPortablePayloadPrepared = false;
        var installationMoved = false;
        IUpdateApplicationProcess? updatedApplication = null;
        try
        {
            await request.VerifyPayloadAsync(cancellationToken).ConfigureAwait(false);
            ValidateMoveLayout(request);
            PrepareTransactionDirectories(request);
            DeleteStaleHealthMarker(request.HealthMarkerPath);
            EnsureMutationTargetsAvailable(request);

            await PortableUpdateData.PrepareStagedPayloadAsync(
                request.InstallationDirectory,
                request.StagingDirectory,
                request.PortableDataMode,
                cancellationToken).ConfigureAwait(false);
            stagedPortablePayloadPrepared = request.PortableDataMode;
            UpdatePathSecurity.EnsureNoReparsePoints(request.StagingDirectory);

            Directory.Move(request.InstallationDirectory, request.BackupDirectory);
            installationMoved = true;
            var backedUp = await TryWriteStateAsync(
                journal,
                journalPath,
                UpdateTransactionState.BackedUp,
                null,
                cancellationToken).ConfigureAwait(false);
            journal = backedUp.Journal;
            if (!backedUp.Persisted)
            {
                return await RollBackCoreAsync(
                    journal,
                    journalPath,
                    "update.backup_journal_failed",
                    CancellationToken.None).ConfigureAwait(false);
            }

            Directory.Move(request.StagingDirectory, request.InstallationDirectory);
            stagedPortablePayloadPrepared = false;
            await VerifyInstalledPayloadAsync(journal, cancellationToken).ConfigureAwait(false);
            var installed = await TryWriteStateAsync(
                journal,
                journalPath,
                UpdateTransactionState.Installed,
                null,
                cancellationToken).ConfigureAwait(false);
            journal = installed.Journal;
            if (!installed.Persisted)
            {
                return await RollBackCoreAsync(
                    journal,
                    journalPath,
                    "update.install_journal_failed",
                    CancellationToken.None).ConfigureAwait(false);
            }

            updatedApplication = _runtime.StartApplication(journal, UpdateApplicationLaunchMode.AfterUpdate);
            var validating = await TryWriteStateAsync(
                journal,
                journalPath,
                UpdateTransactionState.Validating,
                null,
                cancellationToken).ConfigureAwait(false);
            journal = validating.Journal;
            if (!validating.Persisted)
            {
                TryTerminate(updatedApplication);
                return await RollBackCoreAsync(
                    journal,
                    journalPath,
                    "update.validation_journal_failed",
                    CancellationToken.None).ConfigureAwait(false);
            }
            if (await WaitForHealthAsync(
                    updatedApplication,
                    journal,
                    _startupHealthTimeout,
                    cancellationToken).ConfigureAwait(false))
            {
                journal = await WriteStateAsync(
                    journal,
                    journalPath,
                    UpdateTransactionState.Committed,
                    null,
                    CancellationToken.None).ConfigureAwait(false);
                CleanupCommittedTransaction(journal);
                return new UpdateTransactionResult(true, false, null);
            }

            TryTerminate(updatedApplication);
            return await RollBackCoreAsync(
                journal,
                journalPath,
                "update.startup_health_failed",
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryTerminate(updatedApplication);
            if (installationMoved || UpdatePathSecurity.PathEntryExists(request.BackupDirectory))
            {
                return await RollBackCoreAsync(
                    journal,
                    journalPath,
                    "update.install_cancelled",
                    CancellationToken.None).ConfigureAwait(false);
            }

            if (stagedPortablePayloadPrepared)
            {
                PortableUpdateData.RemovePreparedStagedPayload(request.StagingDirectory);
            }

            await TryWriteStateAsync(
                journal,
                journalPath,
                UpdateTransactionState.Failed,
                "update.install_cancelled",
                CancellationToken.None).ConfigureAwait(false);
            TryStartApplication(journal, UpdateApplicationLaunchMode.Normal);
            throw;
        }
        catch (Exception exception) when (IsRecoverableTransactionFailure(exception))
        {
            TryTerminate(updatedApplication);
            if (installationMoved || UpdatePathSecurity.PathEntryExists(request.BackupDirectory))
            {
                return await RollBackCoreAsync(
                    journal,
                    journalPath,
                    "update.install_transaction_failed",
                    CancellationToken.None).ConfigureAwait(false);
            }

            if (stagedPortablePayloadPrepared)
            {
                PortableUpdateData.RemovePreparedStagedPayload(request.StagingDirectory);
            }

            await TryWriteStateAsync(
                journal,
                journalPath,
                UpdateTransactionState.Failed,
                "update.install_transaction_failed",
                CancellationToken.None).ConfigureAwait(false);
            var restarted = TryStartApplication(journal, UpdateApplicationLaunchMode.Normal);
            return new UpdateTransactionResult(
                false,
                false,
                restarted ? "update.install_transaction_failed" : "update.restart_after_failure_failed");
        }
        finally
        {
            updatedApplication?.Dispose();
        }
    }

    public async Task<UpdateTransactionResult> RollBackInterruptedAsync(
        UpdateRollbackRequest request,
        UpdateTransactionJournal journal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(journal);
        var installation = UpdatePathLayout.NormalizeInstallationDirectory(journal.InstallationDirectory);
        var journalPath = UpdatePathLayout.GetTransactionJournalPath(installation, journal.TransactionId);
        using var transactionLock = UpdateTransactionLock.Acquire(installation, journal.TransactionId);
        var current = await UpdateTransactionJournal.ReadAsync(journalPath, cancellationToken).ConfigureAwait(false);
        current.ValidateForInstallation(installation);
        request.ValidateAgainst(current);

        if (current.State is UpdateTransactionState.Committed or UpdateTransactionState.RolledBack)
        {
            return new UpdateTransactionResult(
                current.State is UpdateTransactionState.Committed,
                current.State is UpdateTransactionState.RolledBack,
                current.SafeErrorCode);
        }

        if (current.State is UpdateTransactionState.Prepared or UpdateTransactionState.WaitingForApplicationExit)
        {
            await TryWriteStateAsync(
                current,
                journalPath,
                UpdateTransactionState.Failed,
                "update.rollback_not_required",
                CancellationToken.None).ConfigureAwait(false);
            var restarted = TryStartApplication(current, UpdateApplicationLaunchMode.Normal);
            return new UpdateTransactionResult(
                false,
                false,
                restarted ? "update.rollback_not_required" : "update.restart_after_failure_failed");
        }

        var parentResult = await _runtime.WaitForParentExitAsync(
            request.ParentProcessId,
            request.ParentProcessStartedAtUtc,
            _parentExitTimeout,
            cancellationToken).ConfigureAwait(false);
        if (parentResult is not UpdateParentExitResult.Exited)
        {
            return await HandleParentExitFailureAsync(
                current,
                journalPath,
                parentResult,
                restartWhenIdentityMismatch: true).ConfigureAwait(false);
        }

        return await RollBackCoreAsync(
            current,
            journalPath,
            current.SafeErrorCode ?? "update.interrupted_transaction",
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<UpdateTransactionResult> HandleParentExitFailureAsync(
        UpdateTransactionJournal journal,
        string journalPath,
        UpdateParentExitResult parentResult,
        bool restartWhenIdentityMismatch)
    {
        var safeCode = parentResult is UpdateParentExitResult.IdentityMismatch
            ? "update.parent_identity_mismatch"
            : "update.parent_did_not_exit";
        await TryWriteStateAsync(
            journal,
            journalPath,
            UpdateTransactionState.Failed,
            safeCode,
            CancellationToken.None).ConfigureAwait(false);
        if (restartWhenIdentityMismatch && parentResult is UpdateParentExitResult.IdentityMismatch)
        {
            TryStartApplication(journal, UpdateApplicationLaunchMode.Normal);
        }

        return new UpdateTransactionResult(false, false, safeCode);
    }

    private async Task<UpdateTransactionResult> RollBackCoreAsync(
        UpdateTransactionJournal journal,
        string journalPath,
        string errorCode,
        CancellationToken cancellationToken)
    {
        if (journal.State is not UpdateTransactionState.RollingBack)
        {
            var rollingBack = await TryWriteStateAsync(
                journal,
                journalPath,
                UpdateTransactionState.RollingBack,
                errorCode,
                cancellationToken).ConfigureAwait(false);
            journal = rollingBack.Journal;
        }

        var failedInstallation = UpdatePathLayout.GetFailedInstallationDirectory(
            journal.InstallationDirectory,
            journal.TransactionId);
        var dataCheckpoint = UpdatePathLayout.GetRollbackDataCheckpointDirectory(
            journal.InstallationDirectory,
            journal.TransactionId);
        try
        {
            DeleteStaleHealthMarker(journal.HealthMarkerPath);

            var currentVersion = await ClassifyInstallationAsync(
                journal.InstallationDirectory,
                journal,
                cancellationToken).ConfigureAwait(false);
            var backupVersion = await ClassifyInstallationAsync(
                journal.BackupDirectory,
                journal,
                cancellationToken).ConfigureAwait(false);
            var failedVersion = await ClassifyInstallationAsync(
                failedInstallation,
                journal,
                cancellationToken).ConfigureAwait(false);

            if (currentVersion is RollbackInstallationVersion.Previous)
            {
                if (backupVersion is not RollbackInstallationVersion.Missing)
                {
                    throw new InvalidDataException(
                        "The rollback state contains two competing prior installations.");
                }
            }
            else
            {
                if (backupVersion is not RollbackInstallationVersion.Previous)
                {
                    throw new InvalidDataException(
                        "The update rollback backup is missing or failed integrity verification.");
                }

                if (currentVersion is not RollbackInstallationVersion.Missing)
                {
                    if (failedVersion is not RollbackInstallationVersion.Missing)
                    {
                        throw new InvalidDataException(
                            "The rollback state contains competing failed installations.");
                    }

                    UpdatePathSecurity.EnsureNoReparsePoints(journal.InstallationDirectory);
                    EnsurePathDoesNotExist(
                        failedInstallation,
                        "The failed-installation recovery directory is already occupied.");
                    Directory.Move(journal.InstallationDirectory, failedInstallation);
                    failedVersion = currentVersion;
                    currentVersion = RollbackInstallationVersion.Missing;
                }
                else if (failedVersion is RollbackInstallationVersion.Previous)
                {
                    throw new InvalidDataException(
                        "The failed-installation recovery directory contains the rollback version.");
                }

                if (journal.PortableDataMode &&
                    failedVersion is not RollbackInstallationVersion.Missing)
                {
                    PortableUpdateData.TransferLatestDataForRollback(
                        failedInstallation,
                        journal.BackupDirectory,
                        dataCheckpoint);
                }
                else if (!journal.PortableDataMode && UpdatePathSecurity.PathEntryExists(dataCheckpoint))
                {
                    throw new InvalidDataException(
                        "A non-portable rollback contains an unexpected data checkpoint.");
                }

                EnsurePathDoesNotExist(
                    journal.InstallationDirectory,
                    "The installation path became occupied during rollback.");
                Directory.Move(journal.BackupDirectory, journal.InstallationDirectory);
            }

            await VerifyRestoredInstallationAsync(journal, cancellationToken).ConfigureAwait(false);

            var rolledBack = await TryWriteStateAsync(
                journal,
                journalPath,
                UpdateTransactionState.RolledBack,
                errorCode,
                cancellationToken).ConfigureAwait(false);
            var safeCode = rolledBack.Persisted ? errorCode : "update.rollback_journal_failed";
            var restarted = TryStartApplication(rolledBack.Journal, UpdateApplicationLaunchMode.RolledBack);
            if (!restarted)
            {
                safeCode = "update.rollback_restart_failed";
                await TryWriteStateAsync(
                    rolledBack.Journal,
                    journalPath,
                    UpdateTransactionState.RolledBack,
                    safeCode,
                    CancellationToken.None).ConfigureAwait(false);
            }

            TryDeleteDirectory(failedInstallation);
            PortableUpdateData.CleanupRollbackCheckpoint(dataCheckpoint);
            TryDeleteDirectory(journal.BackupDirectory);
            TryDeleteFile(journal.HealthMarkerPath);
            return new UpdateTransactionResult(false, true, safeCode);
        }
        catch (Exception exception) when (IsRecoverableTransactionFailure(exception))
        {
            var compensation = await TryCompensateRollbackFailureAsync(
                journal,
                failedInstallation,
                dataCheckpoint,
                CancellationToken.None).ConfigureAwait(false);
            if (compensation is RollbackCompensationResult.PreviousVersionRestored)
            {
                var recovered = await TryWriteStateAsync(
                    journal,
                    journalPath,
                    UpdateTransactionState.RolledBack,
                    "update.rollback_recovered_prior_version",
                    CancellationToken.None).ConfigureAwait(false);
                var safeCode = recovered.Persisted
                    ? "update.rollback_recovered_prior_version"
                    : "update.rollback_journal_failed";
                if (!TryStartApplication(recovered.Journal, UpdateApplicationLaunchMode.RolledBack))
                {
                    safeCode = "update.rollback_restart_failed";
                    await TryWriteStateAsync(
                        recovered.Journal,
                        journalPath,
                        UpdateTransactionState.RolledBack,
                        safeCode,
                        CancellationToken.None).ConfigureAwait(false);
                }

                TryDeleteDirectory(failedInstallation);
                PortableUpdateData.CleanupRollbackCheckpoint(dataCheckpoint);
                TryDeleteDirectory(journal.BackupDirectory);
                TryDeleteFile(journal.HealthMarkerPath);
                return new UpdateTransactionResult(false, true, safeCode);
            }

            var failureCode = compensation is RollbackCompensationResult.UpdatedVersionRestored
                ? "update.rollback_failed_install_restored"
                : "update.rollback_failed";
            var failed = await TryWriteStateAsync(
                journal,
                journalPath,
                UpdateTransactionState.Failed,
                failureCode,
                CancellationToken.None).ConfigureAwait(false);
            if (compensation is RollbackCompensationResult.UpdatedVersionRestored &&
                !TryStartApplication(failed.Journal, UpdateApplicationLaunchMode.Normal))
            {
                failureCode = "update.rollback_failed_restart_failed";
                await TryWriteStateAsync(
                    failed.Journal,
                    journalPath,
                    UpdateTransactionState.Failed,
                    failureCode,
                    CancellationToken.None).ConfigureAwait(false);
            }

            return new UpdateTransactionResult(false, false, failureCode);
        }
    }

    private async Task<bool> WaitForHealthAsync(
        IUpdateApplicationProcess application,
        UpdateTransactionJournal journal,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = _runtime.UtcNow + timeout;
        while (_runtime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (application.HasExited)
            {
                return false;
            }

            if (UpdatePathSecurity.PathEntryExists(journal.HealthMarkerPath))
            {
                return await StartupHealthMarker.IsValidAsync(
                    journal,
                    application.ProcessId,
                    application.StartedAtUtc,
                    cancellationToken).ConfigureAwait(false);
            }

            var remaining = deadline - _runtime.UtcNow;
            await _runtime.DelayAsync(
                remaining < HealthPollInterval ? remaining : HealthPollInterval,
                cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static void ValidateMoveLayout(UpdateInstallRequest request)
    {
        UpdatePathSecurity.EnsureSameVolume(
            request.InstallationDirectory,
            request.StagingDirectory,
            "Portable update staging must be on the installation volume.");
        UpdatePathSecurity.EnsureSameVolume(
            request.InstallationDirectory,
            request.BackupDirectory,
            "Portable update backup must be on the installation volume.");
        UpdatePathSecurity.EnsureDirectoryTreeWithoutReparsePoints(
            request.InstallationDirectory,
            "The current installation contains an unsafe filesystem entry.");
        UpdatePathSecurity.EnsureDirectoryTreeWithoutReparsePoints(
            request.StagingDirectory,
            "The staged update contains an unsafe filesystem entry.");
    }

    private static void PrepareTransactionDirectories(UpdateInstallRequest request)
    {
        var backupRoot = Path.GetDirectoryName(request.BackupDirectory)
            ?? throw new InvalidDataException("The update backup root is invalid.");
        var healthRoot = Path.GetDirectoryName(request.HealthMarkerPath)
            ?? throw new InvalidDataException("The update health root is invalid.");
        Directory.CreateDirectory(backupRoot);
        Directory.CreateDirectory(healthRoot);
        UpdatePathSecurity.EnsureDirectory(
            backupRoot,
            "The update backup root is invalid.");
        UpdatePathSecurity.EnsureDirectory(
            healthRoot,
            "The update health root is invalid.");
    }

    private static void EnsureMutationTargetsAvailable(UpdateInstallRequest request)
    {
        EnsurePathDoesNotExist(request.BackupDirectory, "The update backup directory already exists.");
        EnsurePathDoesNotExist(
            UpdatePathLayout.GetFailedInstallationDirectory(request.InstallationDirectory, request.TransactionId),
            "The failed-installation recovery directory already exists.");
        EnsurePathDoesNotExist(
            UpdatePathLayout.GetRollbackDataCheckpointDirectory(request.InstallationDirectory, request.TransactionId),
            "The rollback data checkpoint already exists.");
    }

    private static void EnsurePathDoesNotExist(string path, string safeFailureMessage)
    {
        if (UpdatePathSecurity.PathEntryExists(path))
        {
            throw new IOException(safeFailureMessage);
        }
    }

    private static async Task VerifyInstalledPayloadAsync(
        UpdateTransactionJournal journal,
        CancellationToken cancellationToken)
    {
        UpdatePathSecurity.EnsureNoReparsePoints(journal.InstallationDirectory);
        var manifest = await UpdatePackageFileManifest.ReadAndVerifyAsync(
            journal.InstallationDirectory,
            journal.Version,
            cancellationToken,
            allowPortablePayload: journal.PortableDataMode).ConfigureAwait(false);
        if (!UpdateFileIntegrity.FixedTimeEquals(
                journal.PackageFileManifestSha256,
                manifest.ManifestSha256))
        {
            throw new System.Security.Cryptography.CryptographicException(
                "The installed package integrity manifest changed during the update transaction.");
        }

        await UpdateFileIntegrity.VerifySha256Async(
            Path.Combine(journal.InstallationDirectory, journal.ApplicationExecutableName),
            journal.TargetApplicationSha256,
            "The installed update does not match the verified staged application.",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyRestoredInstallationAsync(
        UpdateTransactionJournal journal,
        CancellationToken cancellationToken)
    {
        UpdatePathSecurity.EnsureDirectoryTreeWithoutReparsePoints(
            journal.InstallationDirectory,
            "The restored installation is unavailable or unsafe.");
        await UpdateFileIntegrity.VerifySha256Async(
            Path.Combine(journal.InstallationDirectory, journal.ApplicationExecutableName),
            journal.ExpectedCurrentApplicationSha256,
            "The restored application failed integrity verification.",
            cancellationToken).ConfigureAwait(false);
        var markerPath = Path.Combine(
            journal.InstallationDirectory,
            PortableUpdateData.MarkerFileName);
        var markerExists = UpdatePathSecurity.PathEntryExists(markerPath);
        if (markerExists)
        {
            UpdatePathSecurity.EnsureRegularFile(
                markerPath,
                "The restored portable-mode marker is invalid.");
        }

        if (markerExists != journal.PortableDataMode)
        {
            throw new InvalidDataException("The restored application portable-data mode is invalid.");
        }
    }

    private static async Task<RollbackCompensationResult> TryCompensateRollbackFailureAsync(
        UpdateTransactionJournal journal,
        string failedInstallation,
        string dataCheckpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            var currentVersion = await ClassifyInstallationAsync(
                journal.InstallationDirectory,
                journal,
                cancellationToken).ConfigureAwait(false);
            if (currentVersion is RollbackInstallationVersion.Previous)
            {
                return RollbackCompensationResult.PreviousVersionRestored;
            }

            if (currentVersion is RollbackInstallationVersion.Target or
                RollbackInstallationVersion.Unknown)
            {
                return await VerifyRestartableInstallationAsync(
                        journal.InstallationDirectory,
                        journal,
                        cancellationToken).ConfigureAwait(false)
                    ? RollbackCompensationResult.UpdatedVersionRestored
                    : RollbackCompensationResult.Failed;
            }

            var failedVersion = await ClassifyInstallationAsync(
                failedInstallation,
                journal,
                cancellationToken).ConfigureAwait(false);
            var backupVersion = await ClassifyInstallationAsync(
                journal.BackupDirectory,
                journal,
                cancellationToken).ConfigureAwait(false);
            if (failedVersion is RollbackInstallationVersion.Target or
                RollbackInstallationVersion.Unknown)
            {
                if (backupVersion is not RollbackInstallationVersion.Missing &&
                    journal.PortableDataMode)
                {
                    PortableUpdateData.RestoreDataAfterFailedRollback(
                        failedInstallation,
                        journal.BackupDirectory,
                        dataCheckpoint);
                }
                else if (!journal.PortableDataMode && UpdatePathSecurity.PathEntryExists(dataCheckpoint))
                {
                    return RollbackCompensationResult.Failed;
                }

                EnsurePathDoesNotExist(
                    journal.InstallationDirectory,
                    "The installation path is occupied during rollback compensation.");
                Directory.Move(failedInstallation, journal.InstallationDirectory);
                return await VerifyRestartableInstallationAsync(
                        journal.InstallationDirectory,
                        journal,
                        cancellationToken).ConfigureAwait(false)
                    ? RollbackCompensationResult.UpdatedVersionRestored
                    : RollbackCompensationResult.Failed;
            }

            if (backupVersion is RollbackInstallationVersion.Previous)
            {
                EnsurePathDoesNotExist(
                    journal.InstallationDirectory,
                    "The installation path is occupied during rollback recovery.");
                Directory.Move(journal.BackupDirectory, journal.InstallationDirectory);
                await VerifyRestoredInstallationAsync(journal, cancellationToken).ConfigureAwait(false);
                return RollbackCompensationResult.PreviousVersionRestored;
            }

            return RollbackCompensationResult.Failed;
        }
        catch (Exception exception) when (IsRecoverableTransactionFailure(exception))
        {
            return RollbackCompensationResult.Failed;
        }
    }

    private static async Task<RollbackInstallationVersion> ClassifyInstallationAsync(
        string directory,
        UpdateTransactionJournal journal,
        CancellationToken cancellationToken)
    {
        if (!UpdatePathSecurity.PathEntryExists(directory))
        {
            return RollbackInstallationVersion.Missing;
        }

        UpdatePathSecurity.EnsureDirectoryTreeWithoutReparsePoints(
            directory,
            "An update installation path is occupied by an invalid filesystem entry.");
        var executable = Path.Combine(directory, journal.ApplicationExecutableName);
        var hash = await UpdateFileIntegrity.ComputeSha256Async(
            executable,
            cancellationToken).ConfigureAwait(false);
        var markerPath = Path.Combine(directory, PortableUpdateData.MarkerFileName);
        var markerExists = UpdatePathSecurity.PathEntryExists(markerPath);
        if (markerExists)
        {
            UpdatePathSecurity.EnsureRegularFile(
                markerPath,
                "The portable-mode marker is invalid during rollback.");
        }

        if (markerExists != journal.PortableDataMode)
        {
            return RollbackInstallationVersion.Unknown;
        }

        if (UpdateFileIntegrity.FixedTimeEquals(
                journal.ExpectedCurrentApplicationSha256,
                hash))
        {
            return RollbackInstallationVersion.Previous;
        }

        return UpdateFileIntegrity.FixedTimeEquals(journal.TargetApplicationSha256, hash)
            ? RollbackInstallationVersion.Target
            : RollbackInstallationVersion.Unknown;
    }

    private static async Task<bool> VerifyRestartableInstallationAsync(
        string directory,
        UpdateTransactionJournal journal,
        CancellationToken cancellationToken)
    {
        try
        {
            UpdatePathSecurity.EnsureDirectoryTreeWithoutReparsePoints(
                directory,
                "The compensated installation is unavailable or unsafe.");
            var executable = Path.Combine(directory, journal.ApplicationExecutableName);
            UpdatePathSecurity.EnsureRegularFile(
                executable,
                "The compensated update executable is unavailable or unsafe.");
            _ = await UpdateFileIntegrity.ComputeSha256Async(
                executable,
                cancellationToken).ConfigureAwait(false);
            var markerPath = Path.Combine(directory, PortableUpdateData.MarkerFileName);
            var markerExists = UpdatePathSecurity.PathEntryExists(markerPath);
            if (markerExists)
            {
                UpdatePathSecurity.EnsureRegularFile(
                    markerPath,
                    "The compensated portable-mode marker is invalid.");
            }

            return markerExists == journal.PortableDataMode;
        }
        catch (Exception exception) when (IsRecoverableTransactionFailure(exception))
        {
            return false;
        }
    }

    private static void CleanupCommittedTransaction(UpdateTransactionJournal journal)
    {
        TryDeleteDirectory(journal.BackupDirectory);
        TryDeleteDirectory(UpdatePathLayout.GetFailedInstallationDirectory(
            journal.InstallationDirectory,
            journal.TransactionId));
        PortableUpdateData.CleanupRollbackCheckpoint(
            UpdatePathLayout.GetRollbackDataCheckpointDirectory(
                journal.InstallationDirectory,
                journal.TransactionId));
        TryDeleteFile(journal.HealthMarkerPath);
    }

    private async Task<UpdateTransactionJournal> WriteStateAsync(
        UpdateTransactionJournal journal,
        string journalPath,
        UpdateTransactionState state,
        string? error,
        CancellationToken cancellationToken)
    {
        var next = journal.WithState(state, _runtime.UtcNow, error);
        await next.WriteAsync(journalPath, cancellationToken).ConfigureAwait(false);
        return next;
    }

    private async Task<StateWriteResult> TryWriteStateAsync(
        UpdateTransactionJournal journal,
        string journalPath,
        UpdateTransactionState state,
        string? error,
        CancellationToken cancellationToken)
    {
        UpdateTransactionJournal next;
        try
        {
            next = journal.WithState(state, _runtime.UtcNow, error);
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
        {
            return new StateWriteResult(journal, false);
        }

        try
        {
            await next.WriteAsync(journalPath, cancellationToken).ConfigureAwait(false);
            return new StateWriteResult(next, true);
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            InvalidOperationException)
        {
            return new StateWriteResult(next, false);
        }
    }

    private bool TryStartApplication(
        UpdateTransactionJournal journal,
        UpdateApplicationLaunchMode launchMode)
    {
        try
        {
            using var process = _runtime.StartApplication(journal, launchMode);
            return true;
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static bool IsRecoverableTransactionFailure(Exception exception) => exception is
        IOException or
        UnauthorizedAccessException or
        InvalidDataException or
        InvalidOperationException or
        System.Security.Cryptography.CryptographicException or
        System.ComponentModel.Win32Exception;

    private static void TryTerminate(IUpdateApplicationProcess? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            process.Terminate();
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or
            ObjectDisposedException or
            System.ComponentModel.Win32Exception)
        {
        }
    }

    private static void DeleteStaleHealthMarker(string path)
    {
        if (!UpdatePathSecurity.PathEntryExists(path))
        {
            return;
        }

        UpdatePathSecurity.EnsureRegularFile(path, "The stale update health marker is unsafe.");
        File.Delete(path);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (UpdatePathSecurity.PathEntryExists(path))
            {
                UpdatePathSecurity.EnsureRegularFile(path, "The update artifact is unsafe to delete.");
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
                    "The update directory is unsafe to delete.");
            }
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            InvalidDataException)
        {
        }
    }

    private enum RollbackInstallationVersion
    {
        Missing,
        Previous,
        Target,
        Unknown,
    }

    private enum RollbackCompensationResult
    {
        Failed,
        UpdatedVersionRestored,
        PreviousVersionRestored,
    }

    private sealed record StateWriteResult(UpdateTransactionJournal Journal, bool Persisted);
}
