using CodexUsageMonitor.Updater.Staging;

namespace CodexUsageMonitor.Updater.Install;

public sealed class PortableUpdateLauncher
{
    private const int MaximumTransactionsPerScan = 256;
    private readonly UpdateArtifactTrustPolicy _trustPolicy;
    private readonly IUpdaterHostStarter _hostStarter;
    private readonly IUpdaterHostFileCopier _hostFileCopier;

    public PortableUpdateLauncher(
        UpdateArtifactTrustPolicy trustPolicy,
        IUpdaterHostStarter hostStarter)
        : this(trustPolicy, hostStarter, new UpdaterHostFileCopier())
    {
    }

    public PortableUpdateLauncher(
        UpdateArtifactTrustPolicy trustPolicy,
        IUpdaterHostStarter hostStarter,
        IUpdaterHostFileCopier hostFileCopier)
    {
        _trustPolicy = trustPolicy ?? throw new ArgumentNullException(nameof(trustPolicy));
        _hostStarter = hostStarter ?? throw new ArgumentNullException(nameof(hostStarter));
        _hostFileCopier = hostFileCopier ?? throw new ArgumentNullException(nameof(hostFileCopier));
    }

    public async Task LaunchAsync(
        UpdateInstallRequest request,
        string publishedUpdaterHostPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(publishedUpdaterHostPath);
        request.ValidateStructure(DateTimeOffset.UtcNow, requireExistingPayload: false);
        using var inventoryLock = UpdateTransactionLock.AcquireInventory(request.InstallationDirectory);
        await EnsureNoConflictingTransactionAsync(request, cancellationToken).ConfigureAwait(false);
        await request.VerifyStagedPayloadAsync(cancellationToken).ConfigureAwait(false);

        var sourceHost = UpdatePathLayout.NormalizePath(publishedUpdaterHostPath);
        UpdatePathSecurity.EnsureExactPath(
            sourceHost,
            Path.Combine(request.StagingDirectory, UpdatePathLayout.UpdaterHostExecutableName),
            "The updater host source is outside the verified staging directory.");
        UpdatePathSecurity.EnsureRegularFile(sourceHost, "The staged updater host is unavailable or unsafe.");
        await _trustPolicy.VerifyPreparedHostAsync(
            sourceHost,
            request.UpdaterHostSha256,
            request.PublisherThumbprints,
            request.TrustMode,
            cancellationToken).ConfigureAwait(false);

        var hostDirectory = UpdatePathLayout.GetUpdaterHostDirectory(
            request.InstallationDirectory,
            request.TransactionId);
        EnsurePathAbsent(hostDirectory, "The updater host transaction directory already exists.");
        Directory.CreateDirectory(hostDirectory);
        UpdatePathSecurity.EnsureNoReparsePoints(hostDirectory);

        var journalPath = UpdatePathLayout.GetTransactionJournalPath(
            request.InstallationDirectory,
            request.TransactionId);
        var requestPath = UpdatePathLayout.GetInstallRequestPath(
            request.InstallationDirectory,
            request.TransactionId);
        var journalWritten = false;
        try
        {
            await _hostFileCopier.CopyAsync(
                sourceHost,
                request.UpdaterHostPath,
                cancellationToken).ConfigureAwait(false);
            await UpdateFileIntegrity.VerifySha256Async(
                request.UpdaterHostPath,
                request.UpdaterHostSha256,
                "The updater host copy failed integrity verification.",
                cancellationToken).ConfigureAwait(false);
            await _trustPolicy.VerifyPreparedHostAsync(
                request.UpdaterHostPath,
                request.UpdaterHostSha256,
                request.PublisherThumbprints,
                request.TrustMode,
                cancellationToken).ConfigureAwait(false);
            await request.VerifyPayloadAsync(cancellationToken).ConfigureAwait(false);

            using (UpdateTransactionLock.Acquire(request.InstallationDirectory, request.TransactionId))
            {
                await request.WriteAsync(requestPath, cancellationToken).ConfigureAwait(false);
                var journal = UpdateTransactionJournal.Create(
                    request,
                    UpdateTransactionState.Prepared,
                    DateTimeOffset.UtcNow);
                await journal.WriteAsync(journalPath, cancellationToken).ConfigureAwait(false);
                journalWritten = true;
            }

            _hostStarter.Start(request.UpdaterHostPath, "--request", requestPath, request.Nonce);
        }
        catch
        {
            if (journalWritten)
            {
                await TryWriteFailureAsync(
                    journalPath,
                    request,
                    "update.host_launch_failed",
                    CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                TryDeleteDirectory(hostDirectory);
            }

            throw;
        }
    }

    public async Task LaunchRollbackAsync(
        UpdateTransactionJournal journal,
        int parentProcessId,
        DateTimeOffset parentProcessStartedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        journal.ValidateForInstallation(journal.InstallationDirectory);
        if (!IsRollbackEligible(journal.State))
        {
            throw new InvalidOperationException("The update transaction is not eligible for rollback.");
        }

        UpdatePathSecurity.EnsureRegularFile(
            journal.UpdaterHostPath,
            "The trusted updater recovery host is unavailable or unsafe.");
        await _trustPolicy.VerifyPreparedHostAsync(
            journal.UpdaterHostPath,
            journal.UpdaterHostSha256,
            journal.PublisherThumbprints,
            journal.TrustMode,
            cancellationToken).ConfigureAwait(false);

        var journalPath = UpdatePathLayout.GetTransactionJournalPath(
            journal.InstallationDirectory,
            journal.TransactionId);
        UpdateRollbackRequest request;
        string requestPath;
        using (UpdateTransactionLock.Acquire(journal.InstallationDirectory, journal.TransactionId))
        {
            var current = await UpdateTransactionJournal.ReadAsync(
                journalPath,
                cancellationToken).ConfigureAwait(false);
            current.ValidateForInstallation(journal.InstallationDirectory);
            if (current.TransactionId != journal.TransactionId || !IsRollbackEligible(current.State))
            {
                throw new InvalidOperationException("The update transaction changed before rollback could be scheduled.");
            }

            await _trustPolicy.VerifyPreparedHostAsync(
                current.UpdaterHostPath,
                current.UpdaterHostSha256,
                current.PublisherThumbprints,
                current.TrustMode,
                cancellationToken).ConfigureAwait(false);
            request = UpdateRollbackRequest.Create(
                current,
                parentProcessId,
                parentProcessStartedAtUtc,
                DateTimeOffset.UtcNow);
            requestPath = UpdatePathLayout.GetRollbackRequestPath(
                current.InstallationDirectory,
                current.TransactionId);
            await request.WriteAsync(requestPath, cancellationToken).ConfigureAwait(false);
            journal = current;
        }

        try
        {
            _hostStarter.Start(
                journal.UpdaterHostPath,
                "--rollback-request",
                requestPath,
                request.Nonce);
        }
        catch
        {
            await TryMarkRollbackLaunchFailureAsync(journalPath, journal, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }

    private static bool IsRollbackEligible(UpdateTransactionState state) => state is
        UpdateTransactionState.BackedUp or
        UpdateTransactionState.Installed or
        UpdateTransactionState.Validating or
        UpdateTransactionState.RollingBack or
        UpdateTransactionState.Failed;

    private static async Task EnsureNoConflictingTransactionAsync(
        UpdateInstallRequest request,
        CancellationToken cancellationToken)
    {
        var transactionRoot = UpdatePathLayout.GetTransactionRoot(request.InstallationDirectory);
        if (!UpdatePathSecurity.PathEntryExists(transactionRoot))
        {
            return;
        }

        UpdatePathSecurity.EnsureDirectoryTreeWithoutReparsePoints(
            transactionRoot,
            "The update transaction inventory is unsafe.");
        var paths = Directory.EnumerateFiles(
                transactionRoot,
                "*.json",
                SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumTransactionsPerScan + 1)
            .ToArray();
        if (paths.Length > MaximumTransactionsPerScan)
        {
            throw new InvalidOperationException(
                "The update transaction inventory exceeds its safety limit.");
        }

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!UpdatePathLayout.TryParseTransactionId(path, out var transactionId))
            {
                throw new InvalidDataException("The update transaction inventory contains an invalid journal name.");
            }

            if (transactionId == request.TransactionId)
            {
                throw new InvalidOperationException("The update transaction identifier has already been used.");
            }

            using var transactionLock = UpdateTransactionLock.TryAcquire(
                request.InstallationDirectory,
                transactionId);
            if (transactionLock is null)
            {
                throw new InvalidOperationException("Another portable update transaction is active.");
            }

            var journal = await UpdateTransactionJournal.ReadAsync(
                path,
                cancellationToken).ConfigureAwait(false);
            journal.ValidateForInstallation(request.InstallationDirectory);
            if (journal.TransactionId != transactionId)
            {
                throw new InvalidDataException(
                    "An update transaction journal does not match its filename.");
            }

            if (journal.State is UpdateTransactionState.Committed or UpdateTransactionState.RolledBack)
            {
                continue;
            }

            if (journal.State is UpdateTransactionState.Failed &&
                !await HasFilesystemMutationAsync(journal, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            throw new InvalidOperationException("Another portable update transaction requires recovery.");
        }
    }

    private static async Task<bool> HasFilesystemMutationAsync(
        UpdateTransactionJournal journal,
        CancellationToken cancellationToken)
    {
        if (UpdatePathSecurity.PathEntryExists(journal.BackupDirectory) ||
            UpdatePathSecurity.PathEntryExists(UpdatePathLayout.GetFailedInstallationDirectory(
                journal.InstallationDirectory,
                journal.TransactionId)) ||
            UpdatePathSecurity.PathEntryExists(UpdatePathLayout.GetRollbackDataCheckpointDirectory(
                journal.InstallationDirectory,
                journal.TransactionId)))
        {
            return true;
        }

        if (!UpdatePathSecurity.PathEntryExists(journal.InstallationDirectory))
        {
            return true;
        }

        try
        {
            UpdatePathSecurity.EnsureDirectoryTreeWithoutReparsePoints(
                journal.InstallationDirectory,
                "The installation is unsafe while checking transaction state.");
            var currentHash = await UpdateFileIntegrity.ComputeSha256Async(
                Path.Combine(journal.InstallationDirectory, journal.ApplicationExecutableName),
                cancellationToken).ConfigureAwait(false);
            return !UpdateFileIntegrity.FixedTimeEquals(
                journal.ExpectedCurrentApplicationSha256,
                currentHash);
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
            return true;
        }
    }

    private static async Task TryWriteFailureAsync(
        string journalPath,
        UpdateInstallRequest request,
        string safeErrorCode,
        CancellationToken cancellationToken)
    {
        try
        {
            using var transactionLock = UpdateTransactionLock.TryAcquire(
                request.InstallationDirectory,
                request.TransactionId);
            if (transactionLock is null)
            {
                return;
            }

            UpdateTransactionJournal failed;
            if (UpdatePathSecurity.PathEntryExists(journalPath))
            {
                UpdatePathSecurity.EnsureRegularFile(
                    journalPath,
                    "The update transaction journal is unsafe.");
                var current = await UpdateTransactionJournal.ReadAsync(
                    journalPath,
                    cancellationToken).ConfigureAwait(false);
                failed = current.WithState(
                    UpdateTransactionState.Failed,
                    DateTimeOffset.UtcNow,
                    safeErrorCode);
            }
            else
            {
                failed = UpdateTransactionJournal.Create(
                    request,
                    UpdateTransactionState.Failed,
                    DateTimeOffset.UtcNow,
                    safeErrorCode);
            }

            await failed.WriteAsync(journalPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            InvalidOperationException)
        {
        }
    }

    private static async Task TryMarkRollbackLaunchFailureAsync(
        string journalPath,
        UpdateTransactionJournal journal,
        CancellationToken cancellationToken)
    {
        try
        {
            using var transactionLock = UpdateTransactionLock.TryAcquire(
                journal.InstallationDirectory,
                journal.TransactionId);
            if (transactionLock is null)
            {
                return;
            }

            var current = await UpdateTransactionJournal.ReadAsync(
                journalPath,
                cancellationToken).ConfigureAwait(false);
            var failed = current.WithState(
                UpdateTransactionState.Failed,
                DateTimeOffset.UtcNow,
                "update.rollback_host_launch_failed");
            await failed.WriteAsync(journalPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            InvalidOperationException)
        {
        }
    }

    private static void EnsurePathAbsent(string path, string safeFailureMessage)
    {
        if (UpdatePathSecurity.PathEntryExists(path))
        {
            throw new IOException(safeFailureMessage);
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
                    "The updater host transaction directory is unsafe to delete.");
            }
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            InvalidDataException)
        {
        }
    }
}
