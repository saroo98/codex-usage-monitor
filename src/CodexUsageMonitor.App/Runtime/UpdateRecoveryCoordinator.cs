using CodexUsageMonitor.App.Services;
using CodexUsageMonitor.Updater.Install;
using CodexUsageMonitor.Windows.Runtime;
using CodexUsageMonitor.Windows.Startup;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.App.Runtime;

public sealed class UpdateRecoveryCoordinator
{
    private readonly PortableUpdateRecovery _recovery;
    private readonly PortableUpdateLauncher _launcher;
    private readonly IApplicationPackageContext _packageContext;
    private readonly IApplicationProcessIdentity _processIdentity;
    private readonly StartupHealthMarkerWriter _healthWriter;
    private readonly UpdateInstallOnExitCoordinator _installOnExit;
    private readonly ApplicationLifetimeController _lifetime;
    private readonly UpdateRuntimeState _state;
    private readonly ApplicationStartupState _startup;
    private readonly ILogger<UpdateRecoveryCoordinator> _logger;

    public UpdateRecoveryCoordinator(
        PortableUpdateRecovery recovery,
        PortableUpdateLauncher launcher,
        IApplicationPackageContext packageContext,
        IApplicationProcessIdentity processIdentity,
        StartupHealthMarkerWriter healthWriter,
        UpdateInstallOnExitCoordinator installOnExit,
        ApplicationLifetimeController lifetime,
        UpdateRuntimeState state,
        ApplicationStartupState startup,
        ILogger<UpdateRecoveryCoordinator> logger)
    {
        _recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _packageContext = packageContext ?? throw new ArgumentNullException(nameof(packageContext));
        _processIdentity = processIdentity ?? throw new ArgumentNullException(nameof(processIdentity));
        _healthWriter = healthWriter ?? throw new ArgumentNullException(nameof(healthWriter));
        _installOnExit = installOnExit ?? throw new ArgumentNullException(nameof(installOnExit));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _startup = startup ?? throw new ArgumentNullException(nameof(startup));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Reconciles interrupted portable-update transactions before normal application
    /// initialization. Returns <see langword="true"/> when a rollback was scheduled and
    /// application shutdown was requested.
    /// </summary>
    public async Task<bool> InspectAsync(CancellationToken cancellationToken)
    {
        if (_packageContext.IsPackaged)
        {
            return false;
        }

        try
        {
            var results = await _recovery.ReconcileAsync(
                AppContext.BaseDirectory,
                UpdateStartupOutcome.Inspection,
                cancellationToken).ConfigureAwait(false);
            ReportResults(results);
            ReportDegradedRecovery(results);
            return await ScheduleRollbackIfRequiredAsync(
                results,
                requestApplicationExit: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedRecoveryFailure(exception))
        {
            SetRecoveryFailure("update.recovery_failed");
            _logger.LogError(
                "Portable update inspection failed with safe code {SafeErrorCode}.",
                "update.recovery_failed");
            return false;
        }
    }

    /// <summary>
    /// Qualifies the running target version, writes its process-bound health marker, and
    /// commits the transaction. Returns <see langword="true"/> when rollback was scheduled
    /// and application shutdown was requested.
    /// </summary>
    public async Task<bool> CompleteHealthyStartupAsync(CancellationToken cancellationToken)
    {
        if (_packageContext.IsPackaged)
        {
            return false;
        }

        try
        {
            var inspection = await _recovery.ReconcileAsync(
                AppContext.BaseDirectory,
                UpdateStartupOutcome.Inspection,
                cancellationToken).ConfigureAwait(false);
            ReportResults(inspection);
            ReportDegradedRecovery(inspection);
            if (await ScheduleRollbackIfRequiredAsync(
                    inspection,
                    requestApplicationExit: true,
                    cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            var healthCandidates = inspection
                .Where(static result =>
                    result.Action is UpdateRecoveryAction.None &&
                    result.Journal is
                    {
                        State: UpdateTransactionState.BackedUp or
                            UpdateTransactionState.Installed or
                            UpdateTransactionState.Validating,
                    })
                .Select(static result => result.Journal!)
                .OrderByDescending(static journal => journal.UpdatedAtUtc)
                .ThenByDescending(static journal => journal.TransactionId)
                .ToArray();
            var healthSelection = UpdateRecoveryPolicy.SelectSingle(healthCandidates
                .Select(static journal => new UpdateRecoveryCandidate(journal.TransactionId, journal.UpdatedAtUtc))
                .ToArray());
            if (healthSelection.Status is UpdateRecoverySelectionStatus.Conflict)
            {
                SetRecoveryFailure("update.recovery_conflicting_transactions");
                _logger.LogError(
                    "Portable update health acknowledgement was rejected with safe code {SafeErrorCode}.",
                    "update.recovery_conflicting_transactions");
                return false;
            }

            if (healthSelection is { Status: UpdateRecoverySelectionStatus.Selected, TransactionId: { } healthTransactionId })
            {
                var journal = healthCandidates.Single(candidate => candidate.TransactionId == healthTransactionId);
                var acknowledged = await _healthWriter.WriteAsync(
                    new StartupHealthRequest(journal.TransactionId, journal.HealthMarkerPath),
                    cancellationToken).ConfigureAwait(false);
                if (!acknowledged)
                {
                    _logger.LogWarning(
                        "Portable update health acknowledgement was withheld with safe code {SafeErrorCode}.",
                        "update.startup_not_qualified");
                }
            }

            var results = await _recovery.ReconcileAsync(
                AppContext.BaseDirectory,
                UpdateStartupOutcome.Healthy,
                cancellationToken).ConfigureAwait(false);
            ReportResults(results);
            ReportDegradedRecovery(results);
            return await ScheduleRollbackIfRequiredAsync(
                results,
                requestApplicationExit: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedRecoveryFailure(exception))
        {
            SetRecoveryFailure("update.recovery_failed");
            _logger.LogError(
                "Portable update startup reconciliation failed with safe code {SafeErrorCode}.",
                "update.recovery_failed");
            return false;
        }
    }

    public async Task<bool> HandleStartupFailureAsync(CancellationToken cancellationToken)
    {
        if (_packageContext.IsPackaged)
        {
            return false;
        }

        try
        {
            var results = await _recovery.ReconcileAsync(
                AppContext.BaseDirectory,
                UpdateStartupOutcome.Failed,
                cancellationToken).ConfigureAwait(false);
            ReportResults(results);
            ReportDegradedRecovery(results);
            return await ScheduleRollbackIfRequiredAsync(
                results,
                requestApplicationExit: false,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedRecoveryFailure(exception))
        {
            SetRecoveryFailure("update.recovery_failed");
            _logger.LogError(
                "Portable update failure recovery could not be scheduled with safe code {SafeErrorCode}.",
                "update.recovery_failed");
            return false;
        }
    }

    private async Task<bool> ScheduleRollbackIfRequiredAsync(
        IReadOnlyList<UpdateRecoveryResult> results,
        bool requestApplicationExit,
        CancellationToken cancellationToken)
    {
        var candidates = results
            .Where(static result =>
                result.Action is UpdateRecoveryAction.RollbackRequired &&
                result.Journal is not null)
            .OrderByDescending(static result => result.Journal!.UpdatedAtUtc)
            .ThenByDescending(static result => result.Journal!.TransactionId)
            .ToArray();
        var selection = UpdateRecoveryPolicy.SelectSingle(candidates
            .Select(static result => new UpdateRecoveryCandidate(result.Journal!.TransactionId, result.Journal.UpdatedAtUtc))
            .ToArray());
        if (selection.Status is UpdateRecoverySelectionStatus.None)
        {
            return false;
        }

        if (selection.Status is UpdateRecoverySelectionStatus.Conflict)
        {
            SetRecoveryFailure("update.recovery_conflicting_transactions");
            _logger.LogError(
                "Portable update rollback scheduling was rejected with safe code {SafeErrorCode}.",
                "update.recovery_conflicting_transactions");
            return false;
        }

        var candidate = candidates.Single(result => result.Journal!.TransactionId == selection.TransactionId);
        var journal = candidate.Journal!;
        try
        {
            _installOnExit.SuppressForRecovery();
            _state.Set(_state.Current with
            {
                Status = UpdateRuntimeStatus.Recovering,
                Progress = null,
                SafeErrorCode = candidate.SafeErrorCode ?? "update.interrupted_transaction",
                CanPrepare = false,
                CanInstall = false,
            });
            await _launcher.LaunchRollbackAsync(
                journal,
                _processIdentity.ProcessId,
                _processIdentity.StartedAtUtc,
                cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(
                "Scheduled rollback for update transaction {TransactionId} from state {PreviousState} with safe code {SafeErrorCode}.",
                journal.TransactionId,
                candidate.PreviousState,
                candidate.SafeErrorCode ?? "update.interrupted_transaction");
            if (requestApplicationExit)
            {
                _lifetime.RequestExit();
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedRecoveryFailure(exception))
        {
            SetRecoveryFailure("update.rollback_schedule_failed");
            _logger.LogError(
                "Rollback scheduling failed for update transaction {TransactionId} with safe code {SafeErrorCode}.",
                journal.TransactionId,
                "update.rollback_schedule_failed");
            return false;
        }
    }

    private void ReportDegradedRecovery(IReadOnlyList<UpdateRecoveryResult> results)
    {
        var failure = results
            .Where(static result => result.Action is UpdateRecoveryAction.Failed)
            .OrderByDescending(static result => result.Journal?.UpdatedAtUtc ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
        if (failure is null)
        {
            _startup.ClearDegraded("portable-update-recovery");
            return;
        }

        _startup.AddDegraded(
            "portable-update-recovery",
            failure.SafeErrorCode ?? "update.recovery_failed");
    }

    private void SetRecoveryFailure(string safeErrorCode)
    {
        _state.Set(_state.Current with
        {
            Status = UpdateRuntimeStatus.Failed,
            Progress = null,
            SafeErrorCode = safeErrorCode,
            CanPrepare = false,
            CanInstall = false,
        });
        _startup.AddDegraded("portable-update-recovery", safeErrorCode);
    }

    private void ReportResults(IReadOnlyList<UpdateRecoveryResult> results)
    {
        foreach (var result in results)
        {
            switch (result.Action)
            {
                case UpdateRecoveryAction.Committed:
                    _logger.LogInformation(
                        "Committed recovered update transaction {TransactionId} from state {PreviousState}.",
                        result.TransactionId,
                        result.PreviousState);
                    break;
                case UpdateRecoveryAction.RolledBack:
                    _logger.LogWarning(
                        "Confirmed rollback of update transaction {TransactionId} from state {PreviousState} with safe code {SafeErrorCode}.",
                        result.TransactionId,
                        result.PreviousState,
                        result.SafeErrorCode ?? "update.interrupted_transaction");
                    break;
                case UpdateRecoveryAction.Failed:
                    _logger.LogWarning(
                        "Update transaction {TransactionId} requires recovery attention with safe code {SafeErrorCode}.",
                        result.TransactionId,
                        result.SafeErrorCode ?? "update.recovery_failed");
                    break;
            }
        }
    }

    private static bool IsExpectedRecoveryFailure(Exception exception) => exception is
        IOException or
        UnauthorizedAccessException or
        InvalidDataException or
        InvalidOperationException or
        System.Security.Cryptography.CryptographicException or
        System.ComponentModel.Win32Exception;
}
