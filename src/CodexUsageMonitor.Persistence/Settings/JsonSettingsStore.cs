using System.Text.Json;
using CodexUsageMonitor.Application.Settings;
using CodexUsageMonitor.Core.Settings;
using CodexUsageMonitor.Persistence.Files;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.Persistence.Settings;

public sealed class JsonSettingsStore : ISettingsStore
{
    private const long MaximumSettingsBytes = 1024 * 1024;
    private readonly string _path;
    private readonly ILogger<JsonSettingsStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonSettingsStore(string path, ILogger<JsonSettingsStore> logger)
    {
        _path = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path)));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<SettingsValidationResult> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
            {
                return SettingsValidation.Normalize(null);
            }

            try
            {
                SettingsMigrationResult migration;
                await using (var stream = OpenRead(_path))
                using (var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    migration = SettingsMigrator.ReadAndMigrate(document.RootElement);
                }

                if (migration.Settings is null)
                {
                    _logger.LogWarning(
                        "Settings could not be loaded ({ErrorCode}); the original file was preserved.",
                        migration.SafeErrorCode);
                    var defaults = SettingsValidation.Normalize(
                        null,
                        canPersist: migration.CanPersist,
                        sourceSchemaVersion: migration.SourceSchemaVersion);
                    return defaults with
                    {
                        Issues = defaults.Issues.Concat([
                            new SettingsValidationIssue("schemaVersion", migration.SafeErrorCode ?? "settings.load_failed"),
                        ]).ToArray(),
                    };
                }

                var result = SettingsValidation.Normalize(
                    migration.Settings,
                    migration.CanPersist,
                    migration.SourceSchemaVersion);
                if (migration.Migrated && result.CanPersist)
                {
                    await SaveCoreAsync(result.Settings, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation(
                        "Settings were migrated from schema {SourceSchema} to {TargetSchema}.",
                        migration.SourceSchemaVersion,
                        AppSettings.CurrentSchemaVersion);
                }

                return result;
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Settings could not be loaded; defaults will be used.");
                QuarantineCorruptSettings();
                return SettingsValidation.Normalize(null) with
                {
                    Issues = [new SettingsValidationIssue("settings", "settings.corrupt_quarantined")],
                };
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var validation = SettingsValidation.Normalize(settings);
        if (!validation.CanPersist)
        {
            throw new InvalidOperationException("The loaded settings schema cannot be overwritten by this application version.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(validation.Settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static FileStream OpenRead(string path)
    {
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            32 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length <= MaximumSettingsBytes)
        {
            return stream;
        }

        stream.Dispose();
        throw new InvalidDataException("Settings file is larger than the accepted limit.");
    }

    private Task SaveCoreAsync(AppSettings settings, CancellationToken cancellationToken) =>
        AtomicFileWriter.WriteAsync(
            _path,
            (stream, token) => JsonSerializer.SerializeAsync(stream, settings, SettingsJson.TypeInfo, token),
            cancellationToken,
            retainBackup: true);

    private void QuarantineCorruptSettings()
    {
        try
        {
            var quarantine = _path + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
            File.Move(_path, quarantine, overwrite: false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
