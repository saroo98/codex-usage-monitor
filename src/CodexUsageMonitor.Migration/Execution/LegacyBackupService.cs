using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using CodexUsageMonitor.Migration.Discovery;

namespace CodexUsageMonitor.Migration.Execution;

public sealed record LegacyBackupFile(string RelativePath, long SizeBytes, string Sha256);

public sealed record LegacyBackupManifest(
    int SchemaVersion,
    DateTimeOffset CreatedAtUtc,
    string? LegacyVersion,
    IReadOnlyList<LegacyBackupFile> Files);

public sealed record LegacyBackupResult(
    string DirectoryPath,
    string ArchivePath,
    long ArchiveSizeBytes,
    string ArchiveSha256);

public sealed class LegacyBackupService
{
    private const long MaximumLegacyFileBytes = 64L * 1024 * 1024;
    private const int MaximumArchiveEntries = 256;

    public async Task<LegacyBackupResult> CreateAsync(
        LegacyInstallation installation,
        string migrationRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationRoot);

        var normalizedRoot = Path.GetFullPath(migrationRoot);
        Directory.CreateDirectory(normalizedRoot);
        var backupName = $"legacy-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        var destination = Path.Combine(normalizedRoot, backupName);
        var archivePath = Path.Combine(normalizedRoot, $"{backupName}.zip");
        Directory.CreateDirectory(destination);
        var files = new List<LegacyBackupFile>();

        try
        {
            foreach (var source in installation.ExistingFiles.Order(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullSource = Path.GetFullPath(source);
                var relative = ResolveRelativePath(installation, fullSource);
                var target = ResolveContainedPath(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await CopyBoundedAsync(fullSource, target, cancellationToken).ConfigureAwait(false);
                var file = await DescribeAsync(target, relative, cancellationToken).ConfigureAwait(false);
                files.Add(file);
            }

            var manifest = new LegacyBackupManifest(
                SchemaVersion: 1,
                DateTimeOffset.UtcNow,
                installation.Version,
                files.AsReadOnly());
            var manifestPath = Path.Combine(destination, "backup-manifest.json");
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
                cancellationToken).ConfigureAwait(false);

            await CreateArchiveAsync(destination, archivePath, cancellationToken).ConfigureAwait(false);
            await VerifyArchiveAsync(archivePath, files.Count + 1, cancellationToken).ConfigureAwait(false);
            var archiveInfo = new FileInfo(archivePath);
            await using var archiveStream = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                8192,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var archiveSha256 = Convert.ToHexStringLower(
                await SHA256.HashDataAsync(archiveStream, cancellationToken).ConfigureAwait(false));
            return new LegacyBackupResult(destination, archivePath, archiveInfo.Length, archiveSha256);
        }
        catch
        {
            TryDeleteDirectory(destination);
            TryDeleteFile(archivePath);
            throw;
        }
    }

    private static string ResolveRelativePath(LegacyInstallation installation, string fullSource)
    {
        var dataDirectory = Path.GetFullPath(installation.DataDirectory);
        var installDirectory = Path.GetFullPath(installation.InstallDirectory);
        if (IsContainedBy(dataDirectory, fullSource))
        {
            return Path.Combine("data", Path.GetRelativePath(dataDirectory, fullSource));
        }

        if (IsContainedBy(installDirectory, fullSource))
        {
            return Path.Combine("install", Path.GetRelativePath(installDirectory, fullSource));
        }

        throw new InvalidDataException("Legacy backup source is outside the discovered installation roots.");
    }

    private static bool IsContainedBy(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return !Path.IsPathRooted(relative) &&
            !relative.Equals("..", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string ResolveContainedPath(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!IsContainedBy(fullRoot, candidate))
        {
            throw new InvalidDataException("Legacy backup destination escaped the migration directory.");
        }

        return candidate;
    }

    private static async Task<LegacyBackupFile> DescribeAsync(
        string path,
        string relative,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            8192,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexStringLower(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        return new LegacyBackupFile(relative.Replace('\\', '/'), info.Length, hash);
    }

    private static async Task CreateArchiveAsync(
        string sourceDirectory,
        string archivePath,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            archivePath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            8192,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var files = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length > MaximumArchiveEntries)
        {
            throw new InvalidDataException("Legacy backup contains too many files.");
        }

        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceDirectory, path).Replace('\\', '/');
            var entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
            await using var input = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                8192,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var entryStream = entry.Open();
            await input.CopyToAsync(entryStream, 8192, cancellationToken).ConfigureAwait(false);
        }

        archive.Dispose();
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyArchiveAsync(
        string archivePath,
        int expectedEntries,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            8192,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count != expectedEntries || archive.Entries.Count > MaximumArchiveEntries)
        {
            throw new InvalidDataException("Legacy backup archive entry count is invalid.");
        }

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                throw new InvalidDataException("Legacy backup archive contains an invalid directory entry.");
            }

            await using var entryStream = entry.Open();
            var buffer = new byte[8192];
            while (await entryStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) > 0)
            {
            }
        }
    }

    private static async Task CopyBoundedAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var info = new FileInfo(source);
        if (info.Length < 0 || info.Length > MaximumLegacyFileBytes)
        {
            throw new InvalidDataException("Legacy file exceeds the migration backup limit.");
        }

        await using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            8192,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            8192,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await input.CopyToAsync(output, 8192, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
