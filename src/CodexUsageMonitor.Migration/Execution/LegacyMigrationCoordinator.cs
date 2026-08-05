using System.Text.Json;
using CodexUsageMonitor.Application.Settings;
using CodexUsageMonitor.Migration.Discovery;
using CodexUsageMonitor.Migration.Legacy;
using CodexUsageMonitor.Migration.Tasks;
using CodexUsageMonitor.Persistence.Files;
using CodexUsageMonitor.Persistence.Paths;
using CodexUsageMonitor.Persistence.Settings;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.Migration.Execution;

public sealed record LegacyMigrationOptions(bool RemoveLegacyScheduledTasks, bool TaskRemovalExplicitlyConfirmed);

public sealed record LegacyMigrationResult(
    bool MigrationFound,
    bool Migrated,
    string? LegacyVersion,
    string? BackupDirectory,
    string? BackupArchive,
    string? BackupArchiveSha256,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<LegacyTaskResult> TaskResults,
    string? SafeErrorCode);

public sealed class LegacyMigrationCoordinator
{
    private readonly LegacyInstallationDiscovery _discovery;
    private readonly LegacyJsonReader _reader;
    private readonly LegacySettingsMapper _mapper;
    private readonly LegacyBackupService _backup;
    private readonly ILegacyScheduledTaskController _tasks;
    private readonly ISettingsStore _settings;
    private readonly AppDataPaths _paths;
    private readonly ILogger<LegacyMigrationCoordinator> _logger;

    public LegacyMigrationCoordinator(
        LegacyInstallationDiscovery discovery,
        LegacyJsonReader reader,
        LegacySettingsMapper mapper,
        LegacyBackupService backup,
        ILegacyScheduledTaskController tasks,
        ISettingsStore settings,
        AppDataPaths paths,
        ILogger<LegacyMigrationCoordinator> logger)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _backup = backup ?? throw new ArgumentNullException(nameof(backup));
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<LegacyMigrationResult> ExecuteAsync(
        LegacyMigrationOptions options,
        CancellationToken cancellationToken)
    {
        var installation = _discovery.Discover();
        if (installation is null)
        {
            return new LegacyMigrationResult(false, false, null, null, null, null, [], [], null);
        }

        var markerPath = Path.Combine(_paths.Root, "migration", "legacy-5x-completed.json");
        if (File.Exists(markerPath))
        {
            return await ReadCompletedMarkerAsync(markerPath, installation.Version, cancellationToken).ConfigureAwait(false);
        }

        if (!File.Exists(installation.ConfigPath))
        {
            return new LegacyMigrationResult(true, false, installation.Version, null, null, null, [], [], "migration.config_missing");
        }

        LegacyBackupResult? backup = null;
        var original = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var config = await _reader.ReadObjectAsync(installation.ConfigPath, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Legacy configuration is missing.");
            var uiState = await _reader.ReadObjectAsync(installation.UiStatePath, cancellationToken).ConfigureAwait(false);
            var state = await _reader.ReadObjectAsync(installation.StatePath, cancellationToken).ConfigureAwait(false);
            var migrationRoot = Path.Combine(_paths.Root, "migration");
            backup = await _backup.CreateAsync(installation, migrationRoot, cancellationToken).ConfigureAwait(false);
            var mapping = _mapper.Map(original.Settings, config, uiState, state);
            await _settings.SaveAsync(mapping.Settings, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<LegacyTaskResult> taskResults = [];
            if (options.RemoveLegacyScheduledTasks)
            {
                taskResults = await _tasks.RemoveKnownTasksAsync(
                    options.TaskRemovalExplicitlyConfirmed,
                    cancellationToken).ConfigureAwait(false);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
            var marker = new
            {
                schemaVersion = 1,
                migratedAtUtc = DateTimeOffset.UtcNow,
                legacyVersion = installation.Version,
                backupDirectory = backup.DirectoryPath,
                backupArchive = backup.ArchivePath,
                backupArchiveSha256 = backup.ArchiveSha256,
                warnings = mapping.Warnings,
                taskResults,
            };
            var markerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
            await AtomicFileWriter.WriteAsync(
                markerPath,
                async (stream, token) =>
                    await JsonSerializer.SerializeAsync(stream, marker, markerOptions, token).ConfigureAwait(false),
                cancellationToken,
                retainBackup: true).ConfigureAwait(false);
            return new LegacyMigrationResult(
                true,
                true,
                installation.Version,
                backup.DirectoryPath,
                backup.ArchivePath,
                backup.ArchiveSha256,
                mapping.Warnings,
                taskResults,
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException)
        {
            _logger.LogError(exception, "Legacy migration failed and settings will be restored.");
            try
            {
                await _settings.SaveAsync(original.Settings, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception restoreException) when (restoreException is IOException or UnauthorizedAccessException)
            {
                _logger.LogCritical(restoreException, "Legacy migration settings rollback failed.");
                return new LegacyMigrationResult(
                    true,
                    false,
                    installation.Version,
                    backup?.DirectoryPath,
                    backup?.ArchivePath,
                    backup?.ArchiveSha256,
                    [],
                    [],
                    "migration.rollback_failed");
            }

            return new LegacyMigrationResult(
                true,
                false,
                installation.Version,
                backup?.DirectoryPath,
                backup?.ArchivePath,
                backup?.ArchiveSha256,
                [],
                [],
                "migration.failed");
        }
    }
    private static async Task<LegacyMigrationResult> ReadCompletedMarkerAsync(
        string markerPath,
        string? discoveredVersion,
        CancellationToken cancellationToken)
    {
        const long maximumMarkerBytes = 2L * 1024 * 1024;
        try
        {
            var info = new FileInfo(markerPath);
            if (info.Length <= 0 || info.Length > maximumMarkerBytes)
            {
                return new LegacyMigrationResult(
                    true,
                    false,
                    discoveredVersion,
                    null,
                    null,
                    null,
                    [],
                    [],
                    "migration.marker_invalid");
            }

            await using var stream = new FileStream(
                markerPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                8192,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var marker = await JsonSerializer.DeserializeAsync<LegacyMigrationMarker>(
                stream,
                new JsonSerializerOptions(JsonSerializerDefaults.Web),
                cancellationToken).ConfigureAwait(false);
            if (marker is not { SchemaVersion: 1 })
            {
                return new LegacyMigrationResult(
                    true,
                    false,
                    discoveredVersion,
                    null,
                    null,
                    null,
                    [],
                    [],
                    "migration.marker_invalid");
            }

            var warnings = (marker.Warnings ?? [])
                .Append("migration.already_completed")
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return new LegacyMigrationResult(
                true,
                false,
                marker.LegacyVersion ?? discoveredVersion,
                marker.BackupDirectory,
                marker.BackupArchive,
                marker.BackupArchiveSha256,
                warnings,
                marker.TaskResults ?? [],
                null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new LegacyMigrationResult(
                true,
                false,
                discoveredVersion,
                null,
                null,
                null,
                [],
                [],
                "migration.marker_invalid");
        }
    }

    private sealed record LegacyMigrationMarker(
        int SchemaVersion,
        DateTimeOffset MigratedAtUtc,
        string? LegacyVersion,
        string? BackupDirectory,
        string? BackupArchive,
        string? BackupArchiveSha256,
        IReadOnlyList<string>? Warnings,
        IReadOnlyList<LegacyTaskResult>? TaskResults);

}
