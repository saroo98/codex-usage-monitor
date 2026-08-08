using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CodexUsageMonitor.Core.Diagnostics;
using CodexUsageMonitor.Core.Settings;
using CodexUsageMonitor.Persistence.Database;
using CodexUsageMonitor.Persistence.Paths;

namespace CodexUsageMonitor.Persistence.Diagnostics;

public sealed class SupportBundleBuilder
{
    private const long MaximumLogBytes = 4L * 1024 * 1024;
    private readonly AppDataPaths _paths;
    private readonly UsageDatabase _database;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public SupportBundleBuilder(AppDataPaths paths, UsageDatabase database)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<string> BuildAsync(
        string destinationPath,
        DiagnosticSnapshot snapshot,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);
        var destination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                await AddJsonAsync(archive, "diagnostics.json", Sanitize(snapshot), cancellationToken).ConfigureAwait(false);
                await AddJsonAsync(archive, "settings.redacted.json", RedactedSettings(settings), cancellationToken).ConfigureAwait(false);
                var integrity = SafeDiagnosticRedactor.Redact(await _database.IntegrityCheckAsync(cancellationToken).ConfigureAwait(false));
                await AddTextAsync(archive, "database-integrity.txt", integrity, cancellationToken).ConfigureAwait(false);
                await AddLogsAsync(archive, cancellationToken).ConfigureAwait(false);
                await AddJsonAsync(archive, "bundle-manifest.json", new
                {
                    schemaVersion = 1,
                    createdAtUtc = DateTimeOffset.UtcNow,
                    privacy = "No database, credentials, OAuth tokens, account labels, email addresses, or raw user paths are included.",
                }, cancellationToken).ConfigureAwait(false);
            }

            SupportBundleSecretScanner.AssertSafe(temporary);
            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task AddLogsAsync(ZipArchive archive, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_paths.LogsDirectory)) return;
        long total = 0;
        foreach (var path in Directory.EnumerateFiles(_paths.LogsDirectory, "*.log*", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(5))
        {
            var info = new FileInfo(path);
            if (info.Length <= 0 || total >= MaximumLogBytes) continue;
            var allowance = (int)Math.Min(MaximumLogBytes - total, Math.Min(info.Length, int.MaxValue));
            var content = await ReadTailAsync(path, allowance, cancellationToken).ConfigureAwait(false);
            var redacted = SafeDiagnosticRedactor.Redact(content);
            await AddTextAsync(archive, $"logs/{Path.GetFileName(path)}.redacted.txt", redacted, cancellationToken).ConfigureAwait(false);
            total += Encoding.UTF8.GetByteCount(redacted);
        }
    }

    private static async Task<string> ReadTailAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = (int)Math.Min(stream.Length, maximumBytes);
        stream.Seek(-length, SeekOrigin.End);
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(buffer);
    }

    private async Task AddJsonAsync(ZipArchive archive, string name, object value, CancellationToken cancellationToken) =>
        await AddTextAsync(archive, name, JsonSerializer.Serialize(value, _jsonOptions), cancellationToken).ConfigureAwait(false);

    private static async Task AddTextAsync(ZipArchive archive, string name, string value, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
        entry.LastWriteTime = DateTimeOffset.UnixEpoch;
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: false);
        await writer.WriteAsync(value.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private static object RedactedSettings(AppSettings settings) => new
    {
        settings.SchemaVersion,
        general = new
        {
            settings.General.StartWithWindows,
            settings.General.CloseToTray,
            settings.General.LaunchMinimized,
            settings.General.PrivacyMode,
            settings.General.Language,
        },
        settings.Widget,
        settings.Limits,
        settings.Notifications,
        email = new
        {
            settings.Email.Provider,
            settings.Email.Enabled,
            accountConfigured = !string.IsNullOrWhiteSpace(settings.Email.ConnectedAddress ?? settings.Email.SenderAddress),
            smtpConfigured = !string.IsNullOrWhiteSpace(settings.Email.SmtpHost),
            credentialConfigured = !string.IsNullOrWhiteSpace(settings.Email.CredentialReference),
            oauthConfigured = !string.IsNullOrWhiteSpace(settings.Email.OAuthTokenReference),
        },
        settings.History,
        settings.Updates,
        profiles = settings.Profiles.Select(static profile => new { profile.Id, profile.Enabled, profile.MonitorInBackground }).ToArray(),
    };

    private static DiagnosticSnapshot Sanitize(DiagnosticSnapshot snapshot) => snapshot with
    {
        OperatingSystem = SafeDiagnosticRedactor.Redact(snapshot.OperatingSystem),
        CodexVersion = SafeDiagnosticRedactor.Redact(snapshot.CodexVersion),
        SafeLastErrorCode = SafeDiagnosticRedactor.Redact(snapshot.SafeLastErrorCode),
        Checks = snapshot.Checks.Select(static check => check with { SafeDetail = SafeDiagnosticRedactor.Redact(check.SafeDetail) }).ToArray(),
        Details = snapshot.Details?.ToDictionary(
            static pair => SafeDiagnosticRedactor.Redact(pair.Key),
            static pair => SafeDiagnosticRedactor.Redact(pair.Value),
            StringComparer.Ordinal),
    };
}
