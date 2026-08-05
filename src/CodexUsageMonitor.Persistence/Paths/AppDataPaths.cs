namespace CodexUsageMonitor.Persistence.Paths;

public sealed record AppDataPaths(
    string Root,
    string SettingsFile,
    string DatabaseFile,
    string LogsDirectory,
    string CacheDirectory,
    string UpdatesDirectory,
    string SupportDirectory,
    bool IsPortable)
{
    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(UpdatesDirectory);
        Directory.CreateDirectory(SupportDirectory);
    }
}
