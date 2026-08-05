using System.Text.Json.Serialization;
using CodexUsageMonitor.Updater.Install;
using CodexUsageMonitor.Updater.Model;

namespace CodexUsageMonitor.Updater.Staging;

public sealed record UpdatePackageFileEntry(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("sha256")] string Sha256);

public sealed record UpdatePackageFileManifest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("files")] IReadOnlyList<UpdatePackageFileEntry> Files)
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumSerializedBytes = 512 * 1024;
    public const int MaximumFileCount = 4096;
    public const int MaximumDirectoryCount = 4096;
    public const long MaximumFileBytes = 512L * 1024 * 1024;
    public const long MaximumPackageBytes = 1024L * 1024 * 1024;

    public static async Task<VerifiedUpdatePackageManifest> ReadAndVerifyAsync(
        string stagingDirectory,
        string expectedVersion,
        CancellationToken cancellationToken,
        bool allowPortablePayload = false)
    {
        var staging = UpdatePathLayout.NormalizePath(stagingDirectory);
        if (!UpdatePathSecurity.PathEntryExists(staging))
        {
            throw new DirectoryNotFoundException("The staged update directory is unavailable.");
        }

        UpdatePathSecurity.EnsureDirectoryTreeWithoutReparsePoints(
            staging,
            "The staged update directory is unsafe.");
        var manifestPath = Path.Combine(staging, UpdatePathLayout.PackageFileManifestName);
        UpdatePathSecurity.EnsureDirectChild(
            manifestPath,
            staging,
            UpdatePathLayout.PackageFileManifestName,
            "The update package file manifest path is invalid.");
        UpdatePathSecurity.EnsureRegularFile(
            manifestPath,
            "The update package file manifest is unavailable or unsafe.");

        var manifest = await BoundedJsonFile.ReadAsync<UpdatePackageFileManifest>(
            manifestPath,
            MaximumSerializedBytes,
            "The update package file manifest is invalid.",
            cancellationToken).ConfigureAwait(false);
        await manifest.ValidateAndVerifyAsync(
            staging,
            expectedVersion,
            allowPortablePayload,
            cancellationToken).ConfigureAwait(false);
        var manifestSha256 = await UpdateFileIntegrity.ComputeSha256Async(
            manifestPath,
            cancellationToken).ConfigureAwait(false);
        return new VerifiedUpdatePackageManifest(manifest, manifestPath, manifestSha256);
    }

    private async Task ValidateAndVerifyAsync(
        string stagingDirectory,
        string expectedVersion,
        bool allowPortablePayload,
        CancellationToken cancellationToken)
    {
        if (SchemaVersion != CurrentSchemaVersion ||
            !SemanticVersion.TryParse(Version, out var parsedVersion) ||
            !string.Equals(parsedVersion.ToString(), Version, StringComparison.Ordinal) ||
            !string.Equals(Version, expectedVersion, StringComparison.Ordinal) ||
            Files is null ||
            Files.Count is <= 0 or > MaximumFileCount)
        {
            throw new InvalidDataException("The update package file manifest metadata is invalid.");
        }

        // Packages target Windows. Reject names that would alias on its case-insensitive filesystem,
        // even when validation is running on a case-sensitive build host.
        var listedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? previousPath = null;
        long totalBytes = 0;
        UpdatePackageFileEntry? applicationEntry = null;
        UpdatePackageFileEntry? updaterHostEntry = null;
        foreach (var entry in Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry is null ||
                !TryNormalizeRelativePath(entry.Path, out var relativePath) ||
                !string.Equals(relativePath, entry.Path, StringComparison.Ordinal) ||
                !listedPaths.Add(relativePath) ||
                (previousPath is not null && string.CompareOrdinal(previousPath, relativePath) >= 0) ||
                entry.SizeBytes is < 0 or > MaximumFileBytes ||
                !IsCanonicalSha256(entry.Sha256))
            {
                throw new InvalidDataException("The update package file manifest contains an invalid entry.");
            }

            if (entry.SizeBytes > MaximumPackageBytes - totalBytes)
            {
                throw new InvalidDataException("The update package file manifest exceeds the package size limit.");
            }

            totalBytes += entry.SizeBytes;
            var filePath = Path.Combine(
                stagingDirectory,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            UpdatePathSecurity.EnsureDescendant(
                filePath,
                stagingDirectory,
                "An update package file escaped the staging directory.");
            UpdatePathSecurity.EnsureRegularFile(
                filePath,
                "An update package file is unavailable or unsafe.");
            var file = new FileInfo(filePath);
            if (file.Length != entry.SizeBytes)
            {
                throw new InvalidDataException("An update package file does not match its manifest metadata.");
            }

            await UpdateFileIntegrity.VerifySha256Async(
                filePath,
                entry.Sha256,
                "An update package file failed integrity verification.",
                cancellationToken).ConfigureAwait(false);

            if (string.Equals(relativePath, UpdatePathLayout.ApplicationExecutableName, StringComparison.Ordinal))
            {
                applicationEntry = entry;
            }
            else if (string.Equals(relativePath, UpdatePathLayout.UpdaterHostExecutableName, StringComparison.Ordinal))
            {
                updaterHostEntry = entry;
            }

            previousPath = relativePath;
        }

        if (applicationEntry is null || updaterHostEntry is null ||
            applicationEntry.SizeBytes <= 0 || updaterHostEntry.SizeBytes <= 0)
        {
            throw new InvalidDataException("The update package manifest does not identify both required executables.");
        }

        if (allowPortablePayload)
        {
            PortableUpdateData.ValidatePreparedPayload(stagingDirectory, cancellationToken);
        }
        else
        {
            PortableUpdateData.ValidateReservedPayloadPaths(stagingDirectory);
        }

        var extractedPaths = EnumerateRegularPackageFiles(
                stagingDirectory,
                cancellationToken,
                allowPortablePayload)
            .Where(path => !string.Equals(
                path,
                UpdatePathLayout.PackageFileManifestName,
                StringComparison.OrdinalIgnoreCase))
            .Where(path => !allowPortablePayload || !IsPortablePayloadPath(path))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        var declaredPaths = listedPaths
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        if (!extractedPaths.SequenceEqual(declaredPaths, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The update package contains files that are missing from its integrity manifest.");
        }
    }

    private static IReadOnlyList<string> EnumerateRegularPackageFiles(
        string root,
        CancellationToken cancellationToken,
        bool ignorePortablePayload)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        var directoryCount = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            UpdatePathSecurity.EnsureNoReparsePoints(directory);
            directoryCount++;
            if (directoryCount > MaximumDirectoryCount)
            {
                throw new InvalidDataException("The update package contains too many directories.");
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         directory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                UpdatePathSecurity.EnsureDescendant(
                    entry,
                    root,
                    "An update package entry escaped the staging directory.");
                var relativePath = Path.GetRelativePath(root, entry)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (ignorePortablePayload && IsPortablePayloadPath(relativePath))
                {
                    continue;
                }

                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("The update package contains a reparse point.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }

                UpdatePathSecurity.EnsureRegularFile(entry, "The update package contains an unsafe file.");
                files.Add(relativePath);
                if (files.Count > MaximumFileCount + 1)
                {
                    throw new InvalidDataException("The update package contains too many files.");
                }
            }
        }

        return files;
    }

    private static bool IsPortablePayloadPath(string relativePath) =>
        string.Equals(relativePath, PortableUpdateData.MarkerFileName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(relativePath, PortableUpdateData.DataDirectoryName, StringComparison.OrdinalIgnoreCase) ||
        relativePath.StartsWith(PortableUpdateData.DataDirectoryName + "/", StringComparison.OrdinalIgnoreCase);

    private static bool TryNormalizeRelativePath(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Contains('\\'))
        {
            return false;
        }

        var segments = value.Split('/');
        if (segments.Any(IsInvalidWindowsPathSegment))
        {
            return false;
        }

        normalized = string.Join('/', segments);
        return !Path.IsPathRooted(normalized) &&
            !string.Equals(normalized, UpdatePathLayout.PackageFileManifestName, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(normalized, PortableUpdateData.MarkerFileName, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(normalized, PortableUpdateData.DataDirectoryName, StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith(PortableUpdateData.DataDirectoryName + "/", StringComparison.OrdinalIgnoreCase);
    }

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

    private static bool IsCanonicalSha256(string? value) =>
        UpdateFileIntegrity.IsSha256(value) &&
        string.Equals(value, value!.ToLowerInvariant(), StringComparison.Ordinal);
}

public sealed record VerifiedUpdatePackageManifest(
    UpdatePackageFileManifest Manifest,
    string ManifestPath,
    string ManifestSha256)
{
    public UpdatePackageFileEntry GetRequiredEntry(string relativePath) =>
        Manifest.Files.Single(entry => string.Equals(entry.Path, relativePath, StringComparison.Ordinal));
}
