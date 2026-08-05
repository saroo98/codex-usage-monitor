namespace CodexUsageMonitor.Updater.Install;

public static class UpdateArtifactCleanup
{
    public static void RemoveExpired(string installationDirectory, DateTimeOffset nowUtc)
    {
        var install = Path.GetFullPath(installationDirectory);
        var root = Path.Combine(Path.GetDirectoryName(install)!, ".codex-usage-monitor-update");
        DeleteChildrenOlderThan(Path.Combine(root, "downloads"), nowUtc.AddDays(-3));
        DeleteChildrenOlderThan(Path.Combine(root, "staging"), nowUtc.AddDays(-3));
        DeleteChildrenOlderThan(Path.Combine(root, "backup"), nowUtc.AddDays(-14));
        DeleteFilesOlderThan(Path.Combine(root, "health"), nowUtc.AddDays(-14));
        DeleteFilesOlderThan(Path.Combine(root, "transactions"), nowUtc.AddDays(-30));
    }

    private static void DeleteChildrenOlderThan(string directory, DateTimeOffset cutoff)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(child) < cutoff.UtcDateTime) Directory.Delete(child, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void DeleteFilesOlderThan(string directory, DateTimeOffset cutoff)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime) File.Delete(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
