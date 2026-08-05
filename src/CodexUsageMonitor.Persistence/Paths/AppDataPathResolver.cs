namespace CodexUsageMonitor.Persistence.Paths;

public static class AppDataPathResolver
{
    public const string PortableMarkerName = "portable.mode";

    public static AppDataPaths Resolve(string applicationBaseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationBaseDirectory);
        var applicationRoot = Path.GetFullPath(applicationBaseDirectory);
        var isPortable = File.Exists(Path.Combine(applicationRoot, PortableMarkerName));
        var root = isPortable
            ? Path.Combine(applicationRoot, "data")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexUsageMonitor");
        root = Path.GetFullPath(root);
        return new AppDataPaths(
            root,
            Path.Combine(root, "settings.json"),
            Path.Combine(root, "usage.db"),
            Path.Combine(root, "logs"),
            Path.Combine(root, "cache"),
            Path.Combine(root, "updates"),
            Path.Combine(root, "support"),
            isPortable);
    }
}
