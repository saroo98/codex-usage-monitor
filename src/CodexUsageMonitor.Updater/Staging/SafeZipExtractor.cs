using System.IO.Compression;
using CodexUsageMonitor.Updater.Install;

namespace CodexUsageMonitor.Updater.Staging;

public sealed class SafeZipExtractor
{
    private const int MaximumEntries = 10_000;
    private const long MaximumExpandedBytes = 1024L * 1024 * 1024;
    private const long MaximumSingleFileBytes = 512L * 1024 * 1024;
    private const int MaximumRelativePathLength = 240;

    public async Task ExtractAsync(
        string archivePath,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        var archiveFullPath = UpdatePathLayout.NormalizePath(archivePath);
        var targetFullPath = UpdatePathLayout.NormalizePath(targetDirectory);
        UpdatePathSecurity.EnsureRegularFile(
            archiveFullPath,
            "The update archive was not found or is unsafe.");

        if (UpdatePathSecurity.PathEntryExists(targetFullPath))
        {
            UpdatePathSecurity.EnsureDirectoryTreeWithoutReparsePoints(
                targetFullPath,
                "The update staging directory is unsafe.");
            if (Directory.EnumerateFileSystemEntries(targetFullPath).Any())
            {
                throw new IOException("Update staging directory must be empty.");
            }
        }
        else
        {
            Directory.CreateDirectory(targetFullPath);
            UpdatePathSecurity.EnsureDirectory(
                targetFullPath,
                "The update staging directory could not be created safely.");
        }

        try
        {
            using var archive = ZipFile.OpenRead(archiveFullPath);
            if (archive.Entries.Count is <= 0 or > MaximumEntries)
            {
                throw new InvalidDataException("Update archive entry count is invalid.");
            }

            var entries = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            long expandedTotal = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalizedRelativePath = ValidateAndNormalizeEntry(entry);
                var isDirectory = IsDirectoryEntry(entry);
                ValidateInventoryEntry(entries, normalizedRelativePath, isDirectory);

                expandedTotal = checked(expandedTotal + entry.Length);
                if (expandedTotal > MaximumExpandedBytes)
                {
                    throw new InvalidDataException("Update archive expands beyond the accepted limit.");
                }

                var destination = Path.Combine(
                    targetFullPath,
                    normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar));
                UpdatePathSecurity.EnsureDescendant(
                    destination,
                    targetFullPath,
                    "Update archive attempted path traversal.");
                if (isDirectory)
                {
                    CreateDirectorySafely(destination);
                    continue;
                }

                var destinationDirectory = Path.GetDirectoryName(destination)
                    ?? throw new InvalidDataException("Update archive contains an invalid file path.");
                CreateDirectorySafely(destinationDirectory);
                if (UpdatePathSecurity.PathEntryExists(destination))
                {
                    throw new InvalidDataException("Update archive contains duplicate or aliased paths.");
                }

                await using var input = entry.Open();
                await using var output = new FileStream(
                    destination,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
                await input.CopyToAsync(output, 128 * 1024, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (output.Length != entry.Length)
                {
                    throw new InvalidDataException("Update archive entry size changed during extraction.");
                }

                UpdatePathSecurity.EnsureRegularFile(
                    destination,
                    "An extracted update file is unsafe.");
            }

            UpdatePathSecurity.EnsureDirectoryTreeWithoutReparsePoints(
                targetFullPath,
                "The extracted update directory contains an unsafe filesystem entry.");
        }
        catch
        {
            TryDeleteDirectory(targetFullPath);
            throw;
        }
    }

    private static string ValidateAndNormalizeEntry(ZipArchiveEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var portablePath = entry.FullName.Replace('\\', '/');
        var isDirectory = IsDirectoryEntry(entry);
        if (portablePath.Length is <= 0 or > MaximumRelativePathLength ||
            portablePath.StartsWith('/') ||
            portablePath.Contains(':', StringComparison.Ordinal) ||
            portablePath.Contains("//", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Update archive contains an unsafe path.");
        }

        var relativePath = isDirectory ? portablePath.TrimEnd('/') : portablePath;
        if (relativePath.Length == 0 ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Split('/').Any(IsInvalidWindowsPathSegment))
        {
            throw new InvalidDataException("Update archive contains an unsafe path.");
        }

        if (entry.Length < 0 ||
            entry.Length > MaximumSingleFileBytes ||
            entry.CompressedLength < 0 ||
            (isDirectory && (entry.Length != 0 || entry.CompressedLength != 0)))
        {
            throw new InvalidDataException("Update archive entry size is invalid.");
        }

        if (entry.Length > 1024 * 1024 &&
            entry.CompressedLength > 0 &&
            entry.Length / Math.Max(1d, entry.CompressedLength) > 1000)
        {
            throw new InvalidDataException("Update archive entry compression ratio is unsafe.");
        }

        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixType is not (0 or 0x4000 or 0x8000) ||
            (isDirectory && unixType == 0x8000) ||
            (!isDirectory && unixType == 0x4000))
        {
            throw new InvalidDataException("Update archive contains a symbolic link or special file.");
        }

        var windowsAttributes = (FileAttributes)(entry.ExternalAttributes & 0xFFFF);
        if ((windowsAttributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            throw new InvalidDataException("Update archive contains a reparse point or device entry.");
        }

        return relativePath;
    }

    private static bool IsDirectoryEntry(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith('/') ||
        entry.FullName.EndsWith('\\') ||
        entry.Name.Length == 0;

    private static bool IsInvalidWindowsPathSegment(string segment)
    {
        if (segment.Length is 0 or > 160 ||
            segment is "." or ".." ||
            segment.EndsWith(' ') ||
            segment.EndsWith('.') ||
            segment.Any(static character =>
                char.IsControl(character) || character is ':' or '*' or '?' or '"' or '<' or '>' or '|'))
        {
            return true;
        }

        var baseName = segment.Split('.', 2, StringSplitOptions.None)[0];
        return baseName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            IsNumberedDevice(baseName, "COM") ||
            IsNumberedDevice(baseName, "LPT");
    }

    private static bool IsNumberedDevice(string value, string prefix) =>
        value.Length == 4 &&
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        value[3] is >= '1' and <= '9';

    private static void ValidateInventoryEntry(
        Dictionary<string, bool> entries,
        string relativePath,
        bool isDirectory)
    {
        if (!entries.TryAdd(relativePath, isDirectory))
        {
            throw new InvalidDataException("Update archive contains duplicate or aliased paths.");
        }

        var ancestor = Path.GetDirectoryName(relativePath.Replace('/', Path.DirectorySeparatorChar));
        while (ancestor is not null)
        {
            ancestor = ancestor.Replace(Path.DirectorySeparatorChar, '/');
            if (entries.TryGetValue(ancestor, out var ancestorIsDirectory) && !ancestorIsDirectory)
            {
                throw new InvalidDataException("Update archive contains a file-directory path collision.");
            }

            ancestor = Path.GetDirectoryName(ancestor.Replace('/', Path.DirectorySeparatorChar));
        }

        if (!isDirectory && entries.Any(pair =>
                pair.Key.StartsWith(relativePath + "/", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Update archive contains a file-directory path collision.");
        }
    }

    private static void CreateDirectorySafely(string path)
    {
        Directory.CreateDirectory(path);
        UpdatePathSecurity.EnsureDirectory(
            path,
            "An update archive directory could not be created safely.");
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (UpdatePathSecurity.PathEntryExists(path))
            {
                UpdatePathSecurity.DeleteDirectoryTree(
                    path,
                    "The update extraction directory is unsafe to delete.");
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
