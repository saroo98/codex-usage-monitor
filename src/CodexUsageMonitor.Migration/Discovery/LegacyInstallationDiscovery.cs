namespace CodexUsageMonitor.Migration.Discovery;

public sealed record LegacyInstallation(
    string DataDirectory,
    string InstallDirectory,
    string? Version,
    string ConfigPath,
    string StatePath,
    string UiStatePath,
    IReadOnlyList<string> ExistingFiles);

public sealed class LegacyInstallationDiscovery
{
    public LegacyInstallation? Discover()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            return null;
        }

        var data = Path.Combine(localAppData, "CodexUsageNotifier");
        var install = Path.Combine(localAppData, "Programs", "CodexUsageNotifier");
        var config = Path.Combine(data, "config.json");
        if (!File.Exists(config) && !Directory.Exists(install))
        {
            return null;
        }

        var versionPath = Path.Combine(install, "VERSION");
        string? version = null;
        try
        {
            if (File.Exists(versionPath))
            {
                var rawVersion = File.ReadAllText(versionPath).Trim();
                version = rawVersion[..Math.Min(rawVersion.Length, 64)];
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        var candidates = new[]
        {
            config,
            Path.Combine(data, "state.json"),
            Path.Combine(data, "state.backup.json"),
            Path.Combine(data, "heartbeat.json"),
            Path.Combine(data, "ui-state.json"),
            Path.Combine(data, "monitor.log"),
            versionPath,
        };
        return new LegacyInstallation(
            data,
            install,
            version,
            config,
            Path.Combine(data, "state.json"),
            Path.Combine(data, "ui-state.json"),
            candidates.Where(File.Exists).ToArray());
    }
}
