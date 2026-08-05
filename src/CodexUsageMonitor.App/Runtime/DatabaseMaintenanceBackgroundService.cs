using CodexUsageMonitor.App.Services;
using CodexUsageMonitor.Persistence.Database;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.App.Runtime;

public sealed class DatabaseMaintenanceBackgroundService : BackgroundService
{
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromHours(24);
    private readonly ApplicationReadinessGate _readiness;
    private readonly UsageDatabase _database;
    private readonly ApplicationSettingsService _settings;
    private readonly ILogger<DatabaseMaintenanceBackgroundService> _logger;

    public DatabaseMaintenanceBackgroundService(
        ApplicationReadinessGate readiness,
        UsageDatabase database,
        ApplicationSettingsService settings,
        ILogger<DatabaseMaintenanceBackgroundService> logger)
    {
        _readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _readiness.WaitAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(MaintenanceInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await _database.MaintainAsync(stoppingToken).ConfigureAwait(false);
                _logger.LogDebug(
                    "Database maintenance completed with {RetentionDays}-day history retention configured.",
                    _settings.Current.History.RetentionDays);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException)
            {
                _logger.LogWarning(exception, "Database maintenance failed and will be retried later.");
            }
        }
    }
}
