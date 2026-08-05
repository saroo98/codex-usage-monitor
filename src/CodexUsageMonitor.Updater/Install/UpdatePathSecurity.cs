namespace CodexUsageMonitor.Updater.Install;

public static class UpdatePathSecurity
{
    private const int MaximumTreeEntries = 100_000;

    public static bool PathEntryExists(string path)
    {
        var fullPath = UpdatePathLayout.NormalizePath(path);
        try
        {
            _ = File.GetAttributes(fullPath);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("An update path entry could not be inspected safely.", exception);
        }
    }

    public static void EnsureNoReparsePoints(string path)
    {
        var fullPath = UpdatePathLayout.NormalizePath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidDataException("The update path has no filesystem root.");
        var relative = fullPath[root.Length..];
        var current = root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!TryGetAttributes(current, out var attributes, "An update path component could not be inspected safely."))
            {
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Update paths cannot contain reparse points.");
            }
        }
    }

    public static void EnsureDirectory(string path, string safeFailureMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeFailureMessage);
        var fullPath = UpdatePathLayout.NormalizePath(path);
        EnsureNoReparsePoints(fullPath);
        if (!TryGetAttributes(fullPath, out var attributes, safeFailureMessage))
        {
            throw new DirectoryNotFoundException(safeFailureMessage);
        }

        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            throw new InvalidDataException(safeFailureMessage);
        }
    }

    public static void EnsureDirectoryTreeWithoutReparsePoints(string path, string safeFailureMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeFailureMessage);
        var fullPath = UpdatePathLayout.NormalizePath(path);
        EnsureDirectory(fullPath, safeFailureMessage);

        var pending = new Stack<string>();
        pending.Push(fullPath);
        var inspected = 0;
        var enumerationOptions = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory, "*", enumerationOptions);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException(safeFailureMessage, exception);
            }

            try
            {
                foreach (var entry in entries)
                {
                    if (++inspected > MaximumTreeEntries ||
                        !TryGetAttributes(entry, out var attributes, safeFailureMessage) ||
                        (attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    {
                        throw new InvalidDataException(safeFailureMessage);
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException(safeFailureMessage, exception);
            }
        }
    }

    public static void DeleteDirectoryTree(string path, string safeFailureMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeFailureMessage);
        var fullPath = UpdatePathLayout.NormalizePath(path);
        if (!PathEntryExists(fullPath))
        {
            return;
        }

        EnsureDirectoryTreeWithoutReparsePoints(fullPath, safeFailureMessage);
        try
        {
            Directory.Delete(fullPath, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException(safeFailureMessage, exception);
        }
    }

    public static void EnsureDescendant(string path, string root, string safeFailureMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeFailureMessage);
        var normalizedPath = UpdatePathLayout.NormalizePath(path);
        var normalizedRoot = UpdatePathLayout.NormalizePath(root);
        string relative;
        try
        {
            relative = Path.GetRelativePath(normalizedRoot, normalizedPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException(safeFailureMessage, exception);
        }

        if (relative.Length == 0 ||
            string.Equals(relative, ".", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative) ||
            string.Equals(relative, "..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException(safeFailureMessage);
        }
    }

    public static void EnsureDirectChild(
        string path,
        string root,
        string expectedName,
        string safeFailureMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedName);
        if (!string.Equals(Path.GetFileName(expectedName), expectedName, StringComparison.Ordinal) ||
            expectedName is "." or "..")
        {
            throw new InvalidDataException(safeFailureMessage);
        }

        EnsureExactPath(
            path,
            Path.Combine(UpdatePathLayout.NormalizePath(root), expectedName),
            safeFailureMessage);
    }

    public static void EnsureExactPath(string actual, string expected, string safeFailureMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeFailureMessage);
        if (!string.Equals(
                UpdatePathLayout.NormalizePath(actual),
                UpdatePathLayout.NormalizePath(expected),
                UpdatePathLayout.PathComparison))
        {
            throw new InvalidDataException(safeFailureMessage);
        }
    }

    public static void EnsureSameVolume(string left, string right, string safeFailureMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeFailureMessage);
        var leftRoot = Path.GetPathRoot(UpdatePathLayout.NormalizePath(left));
        var rightRoot = Path.GetPathRoot(UpdatePathLayout.NormalizePath(right));
        if (string.IsNullOrEmpty(leftRoot) ||
            string.IsNullOrEmpty(rightRoot) ||
            !string.Equals(
                Path.TrimEndingDirectorySeparator(leftRoot),
                Path.TrimEndingDirectorySeparator(rightRoot),
                UpdatePathLayout.PathComparison))
        {
            throw new InvalidDataException(safeFailureMessage);
        }
    }

    public static void EnsureRegularFile(string path, string safeFailureMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeFailureMessage);
        var fullPath = UpdatePathLayout.NormalizePath(path);
        EnsureNoReparsePoints(fullPath);
        if (!TryGetAttributes(fullPath, out var attributes, safeFailureMessage))
        {
            throw new FileNotFoundException(safeFailureMessage, fullPath);
        }

        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            throw new InvalidDataException(safeFailureMessage);
        }
    }

    public static void EnsureHostInvocationRequestPath(
        string runningUpdaterHostPath,
        string requestPath,
        string expectedRequestFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRequestFileName);
        var host = UpdatePathLayout.NormalizePath(runningUpdaterHostPath);
        var hostDirectory = Path.GetDirectoryName(host)
            ?? throw new InvalidDataException("The updater host path has no parent directory.");
        EnsureDirectChild(
            requestPath,
            hostDirectory,
            expectedRequestFileName,
            "The updater request is outside the trusted host transaction directory.");
        EnsureRegularFile(host, "The updater host path is invalid.");
        EnsureRegularFile(requestPath, "The updater request path is invalid.");
    }

    private static bool TryGetAttributes(
        string path,
        out FileAttributes attributes,
        string safeFailureMessage)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(safeFailureMessage, exception);
        }
    }
}
