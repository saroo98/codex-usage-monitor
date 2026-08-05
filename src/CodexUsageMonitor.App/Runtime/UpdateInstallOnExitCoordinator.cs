using CodexUsageMonitor.App.Services;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.App.Runtime;

public sealed class UpdateInstallOnExitCoordinator
{
    private readonly ApplicationSettingsService _settings;
    private readonly UpdateCoordinatorService _updates;
    private readonly ILogger<UpdateInstallOnExitCoordinator> _logger;
    private int _suppressedForRecovery;

    public UpdateInstallOnExitCoordinator(
        ApplicationSettingsService settings,
        UpdateCoordinatorService updates,
        ILogger<UpdateInstallOnExitCoordinator> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _updates = updates ?? throw new ArgumentNullException(nameof(updates));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void SuppressForRecovery() => Interlocked.Exchange(ref _suppressedForRecovery, 1);

    public async Task PrepareExitAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _suppressedForRecovery) != 0 ||
            !_settings.Current.Updates.InstallOnExit || !_updates.Current.CanInstall)
        {
            return;
        }

        var result = await _updates.InstallPreparedAsync(cancellationToken).ConfigureAwait(false);
        if (result.Status is not UpdateRuntimeStatus.Installing)
        {
            _logger.LogWarning(
                "A prepared update was not launched during exit. Status {Status}; code {SafeErrorCode}.",
                result.Status,
                result.SafeErrorCode ?? "update.install_not_started");
        }
    }
}
