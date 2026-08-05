namespace CodexUsageMonitor.Updater.Install;

internal static class PortableUpdateData
{
    internal const string MarkerFileName = "portable.mode";
    internal const string DataDirectoryName = "data";
    private const int MaximumEntryCount = 100_000;

    public static void ValidateReservedPayloadPaths(string stagingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        var staging = UpdatePathLayout.NormalizePath(stagingDirectory);
        if (!UpdatePathSecurity.PathEntryExists(staging))
        {
            return;
        }

        UpdatePathSecurity.EnsureDirectoryTreeWithoutReparsePoints(
            staging,
            "The staged update directory is invalid.");
        RejectReservedDirectChildren(staging);
    }

    public static void ValidatePreparedPayload(
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        var staging = UpdatePathLayout.NormalizePath(stagingDirectory);
        UpdatePathSecurity.EnsureDirectoryTreeWithoutReparsePoints(
            staging,
            "The staged portable update directory is unavailable or unsafe.");
        var marker = GetCanonicalReservedDirectChild(staging, MarkerFileName);
        var data = GetCanonicalReservedDirectChild(staging, DataDirectoryName);
        UpdatePathSecurity.EnsureRegularFile(marker, "The staged portable-mode marker is invalid.");
        if (new FileInfo(marker).Length != 0)
        {
            throw new InvalidDataException("The staged portable-mode marker is invalid.");
        }

        UpdatePathSecurity.EnsureDirectory(
            data,
            "The staged portable user data directory is unavailable.");
        ValidateDirectoryTree(data, cancellationToken);
    }

    public static async Task PrepareStagedPayloadAsync(
        string installationDirectory,
        string stagingDirectory,
        bool portableMode,
        CancellationToken cancellationToken)
    {
        var installation = UpdatePathLayout.NormalizeInstallationDirectory(installationDirectory);
        var staging = UpdatePathLayout.NormalizePath(stagingDirectory);
        UpdatePathSecurity.EnsureNoReparsePoints(installation);
        UpdatePathSecurity.EnsureNoReparsePoints(staging);
        ValidateReservedPayloadPaths(staging);

        var marker = Path.Combine(installation, MarkerFileName);
        var sourceData = Path.Combine(installation, DataDirectoryName);
        var markerExists = UpdatePathSecurity.PathEntryExists(marker);
        if (markerExists != portableMode)
        {
            throw new InvalidDataException("Portable data mode changed after the update was prepared.");
        }

        if (!portableMode)
        {
            if (UpdatePathSecurity.PathEntryExists(sourceData))
            {
                throw new InvalidDataException("Portable user data exists without the portable-mode marker.");
            }

            return;
        }

        UpdatePathSecurity.EnsureRegularFile(marker, "The portable-mode marker is invalid.");
        var markerInfo = new FileInfo(marker);
        if (markerInfo.Length > 128)
        {
            throw new InvalidDataException("The portable-mode marker is invalid.");
        }

        var sourceDataExists = UpdatePathSecurity.PathEntryExists(sourceData);
        if (sourceDataExists)
        {
            UpdatePathSecurity.EnsureDirectory(
                sourceData,
                "The portable user data path is invalid.");
            ValidateDirectoryTree(sourceData, cancellationToken);
        }

        var stagedMarker = Path.Combine(staging, MarkerFileName);
        var stagedData = Path.Combine(staging, DataDirectoryName);
        try
        {
            await WriteMarkerAsync(stagedMarker, cancellationToken).ConfigureAwait(false);
            if (sourceDataExists)
            {
                await CopyDirectoryAsync(sourceData, stagedData, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Directory.CreateDirectory(stagedData);
                UpdatePathSecurity.EnsureDirectory(
                    stagedData,
                    "The staged portable user data directory is invalid.");
            }
        }
        catch
        {
            TryDeleteFile(stagedMarker);
            TryDeleteDirectory(stagedData);
            throw;
        }
    }

    public static void TransferLatestDataForRollback(
        string updatedInstallationDirectory,
        string previousInstallationDirectory,
        string staleDataDirectory)
    {
        var updated = UpdatePathLayout.NormalizePath(updatedInstallationDirectory);
        var previous = UpdatePathLayout.NormalizePath(previousInstallationDirectory);
        var stale = UpdatePathLayout.NormalizePath(staleDataDirectory);
        var updatedMarker = Path.Combine(updated, MarkerFileName);
        var previousMarker = Path.Combine(previous, MarkerFileName);
        var updatedData = Path.Combine(updated, DataDirectoryName);
        var previousData = Path.Combine(previous, DataDirectoryName);

        UpdatePathSecurity.EnsureRegularFile(
            updatedMarker,
            "The updated portable-mode marker is unavailable during rollback.");
        UpdatePathSecurity.EnsureRegularFile(
            previousMarker,
            "The previous portable-mode marker is unavailable during rollback.");
        UpdatePathSecurity.EnsureNoReparsePoints(updated);
        UpdatePathSecurity.EnsureNoReparsePoints(previous);
        UpdatePathSecurity.EnsureNoReparsePoints(Path.GetDirectoryName(stale)!);

        var updatedDataExists = ValidateOptionalDirectory(
            updatedData,
            "The updated portable user data path is invalid.");
        var previousDataExists = ValidateOptionalDirectory(
            previousData,
            "The previous portable user data path is invalid.");
        var staleDataExists = ValidateOptionalDirectory(
            stale,
            "The portable rollback data checkpoint is invalid.");

        if (staleDataExists)
        {
            if (updatedDataExists && previousDataExists)
            {
                throw new InvalidDataException(
                    "The portable rollback data state is ambiguous and cannot be resumed safely.");
            }

            if (!previousDataExists)
            {
                if (!updatedDataExists)
                {
                    throw new InvalidDataException(
                        "Portable user data is unavailable in an interrupted rollback.");
                }

                Directory.Move(updatedData, previousData);
                return;
            }

            if (updatedDataExists)
            {
                throw new InvalidDataException(
                    "The portable rollback data state contains competing current data.");
            }

            return;
        }

        if (!updatedDataExists)
        {
            if (!previousDataExists)
            {
                throw new InvalidDataException(
                    "Portable user data could not be preserved during rollback.");
            }

            return;
        }

        if (previousDataExists)
        {
            Directory.Move(previousData, stale);
        }

        try
        {
            Directory.Move(updatedData, previousData);
        }
        catch
        {
            if (!UpdatePathSecurity.PathEntryExists(previousData) &&
                UpdatePathSecurity.PathEntryExists(stale))
            {
                UpdatePathSecurity.EnsureDirectory(
                    stale,
                    "The portable rollback data checkpoint is invalid.");
                Directory.Move(stale, previousData);
            }

            throw;
        }
    }

    public static void RestoreDataAfterFailedRollback(
        string updatedInstallationDirectory,
        string previousInstallationDirectory,
        string staleDataDirectory)
    {
        var updated = UpdatePathLayout.NormalizePath(updatedInstallationDirectory);
        var previous = UpdatePathLayout.NormalizePath(previousInstallationDirectory);
        var stale = UpdatePathLayout.NormalizePath(staleDataDirectory);
        var previousData = Path.Combine(previous, DataDirectoryName);
        var updatedData = Path.Combine(updated, DataDirectoryName);

        UpdatePathSecurity.EnsureNoReparsePoints(previous);
        UpdatePathSecurity.EnsureNoReparsePoints(updated);
        UpdatePathSecurity.EnsureNoReparsePoints(Path.GetDirectoryName(stale)!);
        var updatedDataExists = ValidateOptionalDirectory(
            updatedData,
            "The updated portable user data path is invalid during rollback recovery.");
        var previousDataExists = ValidateOptionalDirectory(
            previousData,
            "The previous portable user data path is invalid during rollback recovery.");
        var staleDataExists = ValidateOptionalDirectory(
            stale,
            "The portable rollback data checkpoint is invalid during recovery.");

        if (staleDataExists)
        {
            if (updatedDataExists && previousDataExists)
            {
                throw new InvalidDataException(
                    "The portable rollback data state is ambiguous during compensation.");
            }

            if (updatedDataExists)
            {
                Directory.Move(stale, previousData);
                return;
            }

            if (!previousDataExists)
            {
                throw new InvalidDataException(
                    "Portable user data is unavailable during rollback compensation.");
            }

            Directory.Move(previousData, updatedData);
            Directory.Move(stale, previousData);
            return;
        }

        if (!updatedDataExists && previousDataExists)
        {
            Directory.Move(previousData, updatedData);
        }
    }

    public static void RemovePreparedStagedPayload(string stagingDirectory)
    {
        var staging = UpdatePathLayout.NormalizePath(stagingDirectory);
        TryDeleteFile(Path.Combine(staging, MarkerFileName));
        TryDeleteDirectory(Path.Combine(staging, DataDirectoryName));
    }

    public static void CleanupRollbackCheckpoint(string staleDataDirectory) =>
        TryDeleteDirectory(staleDataDirectory);

    private static bool ValidateOptionalDirectory(string path, string safeFailureMessage)
    {
        if (!UpdatePathSecurity.PathEntryExists(path))
        {
            return false;
        }

        UpdatePathSecurity.EnsureDirectory(path, safeFailureMessage);
        ValidateDirectoryTree(path, CancellationToken.None);
        return true;
    }

    private static async Task CopyDirectoryAsync(
        string sourceDirectory,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        ValidateDirectoryTree(sourceDirectory, cancellationToken);
        RejectExistingPath(destinationDirectory, "The staged portable data directory already exists.");
        Directory.CreateDirectory(destinationDirectory);
        UpdatePathSecurity.EnsureDirectory(
            destinationDirectory,
            "The staged portable data directory is invalid.");

        var pending = new Stack<(string Source, string Destination)>();
        pending.Push((sourceDirectory, destinationDirectory));
        var entryCount = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (source, destination) = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         source,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                entryCount++;
                if (entryCount > MaximumEntryCount)
                {
                    throw new InvalidDataException("Portable user data contains too many entries for a safe update.");
                }

                var target = Path.Combine(destination, Path.GetFileName(entry));
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Portable user data cannot contain reparse points.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Directory.CreateDirectory(target);
                    pending.Push((entry, target));
                    continue;
                }

                UpdatePathSecurity.EnsureRegularFile(entry, "Portable user data contains an invalid file.");
                await CopyFileAsync(entry, target, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var lastWriteTimeUtc = File.GetLastWriteTimeUtc(sourcePath);
        await using (var source = new FileStream(
                         sourcePath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         128 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var target = new FileStream(
                         destinationPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         128 * 1024,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.SetLastWriteTimeUtc(destinationPath, lastWriteTimeUtc);
    }

    private static void ValidateDirectoryTree(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var entryCount = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            UpdatePathSecurity.EnsureNoReparsePoints(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         directory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                entryCount++;
                if (entryCount > MaximumEntryCount)
                {
                    throw new InvalidDataException("Portable user data contains too many entries for a safe update.");
                }

                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Portable user data cannot contain reparse points.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else
                {
                    UpdatePathSecurity.EnsureRegularFile(entry, "Portable user data contains an invalid file.");
                }
            }
        }
    }

    private static async Task WriteMarkerAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void RejectReservedDirectChildren(string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(entry);
            if (string.Equals(name, MarkerFileName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Update packages cannot supply the portable-mode marker.");
            }

            if (string.Equals(name, DataDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Update packages cannot supply or replace portable user data.");
            }
        }
    }

    private static string GetCanonicalReservedDirectChild(string root, string expectedName)
    {
        string? match = null;
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly))
        {
            if (!string.Equals(Path.GetFileName(entry), expectedName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (match is not null || !string.Equals(Path.GetFileName(entry), expectedName, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The staged portable payload contains an ambiguous reserved path.");
            }

            match = entry;
        }

        return match ?? throw new InvalidDataException("The staged portable payload is incomplete.");
    }

    private static void RejectExistingPath(string path, string message)
    {
        if (UpdatePathSecurity.PathEntryExists(path))
        {
            throw new InvalidDataException(message);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (UpdatePathSecurity.PathEntryExists(path))
            {
                UpdatePathSecurity.EnsureRegularFile(path, "The portable update artifact is unsafe to delete.");
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            InvalidDataException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (UpdatePathSecurity.PathEntryExists(path))
            {
                UpdatePathSecurity.DeleteDirectoryTree(
                    path,
                    "The portable update directory is unsafe to delete.");
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
