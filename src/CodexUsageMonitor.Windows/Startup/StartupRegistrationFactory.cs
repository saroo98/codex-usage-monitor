using System.Runtime.InteropServices;

namespace CodexUsageMonitor.Windows.Startup;

public static class StartupRegistrationFactory
{
    public static IStartupRegistration Create(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        return IsPackaged()
            ? new PackagedStartupRegistration("CodexUsageMonitorStartup")
            : new PortableStartupRegistration("Codex Usage Monitor", executablePath);
    }

    public static bool IsPackaged()
    {
        var length = 0u;
        var result = NativeMethods.GetCurrentPackageFullName(ref length, null);
        return result is NativeMethods.ErrorInsufficientBuffer;
    }

    private static class NativeMethods
    {
        internal const int ErrorInsufficientBuffer = 122;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, char[]? packageFullName);
    }
}
