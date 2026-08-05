namespace CodexUsageMonitor.Windows.Startup;

/// <summary>
/// Exposes whether the current process has Windows package identity. Keeping
/// this behind an interface makes package-specific behavior deterministic in
/// tests and prevents portable update code from running inside MSIX.
/// </summary>
public interface IApplicationPackageContext
{
    bool IsPackaged { get; }
}

public sealed class WindowsApplicationPackageContext : IApplicationPackageContext
{
    public bool IsPackaged => StartupRegistrationFactory.IsPackaged();
}
