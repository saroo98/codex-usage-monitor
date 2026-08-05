using CodexUsageMonitor.Core.Abstractions;
using CodexUsageMonitor.Updater.Install;
using CodexUsageMonitor.Windows.Runtime;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.App.Runtime;

public sealed class StartupHealthMarkerWriter
{
    private readonly IClock _clock;
    private readonly IApplicationProcessIdentity _processIdentity;
    private readonly StartupHealthQualification _qualification;
    private readonly ILogger<StartupHealthMarkerWriter> _logger;

    public StartupHealthMarkerWriter(
        IClock clock,
        IApplicationProcessIdentity processIdentity,
        StartupHealthQualification qualification,
        ILogger<StartupHealthMarkerWriter> logger)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _processIdentity = processIdentity ?? throw new ArgumentNullException(nameof(processIdentity));
        _qualification = qualification ?? throw new ArgumentNullException(nameof(qualification));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> WriteAsync(StartupHealthRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var installation = UpdatePathLayout.NormalizeInstallationDirectory(AppContext.BaseDirectory);
            var marker = UpdatePathLayout.NormalizePath(request.HealthMarkerPath);
            UpdatePathSecurity.EnsureExactPath(
                marker,
                UpdatePathLayout.GetHealthMarkerPath(installation, request.TransactionId),
                "The startup health marker is outside the active update transaction.");

            var journalPath = UpdatePathLayout.GetTransactionJournalPath(installation, request.TransactionId);
            var journal = await UpdateTransactionJournal.ReadAsync(journalPath, cancellationToken).ConfigureAwait(false);
            journal.ValidateForInstallation(installation);
            if (journal.TransactionId != request.TransactionId ||
                journal.State is not (UpdateTransactionState.BackedUp or
                    UpdateTransactionState.Installed or
                    UpdateTransactionState.Validating))
            {
                throw new InvalidDataException("The startup health request does not match an active update transaction.");
            }

            UpdatePathSecurity.EnsureExactPath(
                journal.HealthMarkerPath,
                marker,
                "The startup health marker does not match the transaction journal.");
            await UpdateFileIntegrity.VerifySha256Async(
                Path.Combine(installation, journal.ApplicationExecutableName),
                journal.TargetApplicationSha256,
                "The running application does not match the verified update payload.",
                cancellationToken).ConfigureAwait(false);
            if (!await _qualification.WaitAsync(cancellationToken).ConfigureAwait(false))
            {
                _logger.LogWarning(
                    "Startup health acknowledgement was withheld with safe code {SafeCode}.",
                    "update.startup_not_qualified");
                return false;
            }

            await StartupHealthMarker.WriteAsync(
                journal,
                _processIdentity.ProcessId,
                _processIdentity.StartedAtUtc,
                _clock.UtcNow,
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            System.Security.Cryptography.CryptographicException)
        {
            _logger.LogError(
                "Startup health acknowledgement failed with safe code {SafeCode}.",
                "update.health_marker_failed");
            return false;
        }
    }
}
