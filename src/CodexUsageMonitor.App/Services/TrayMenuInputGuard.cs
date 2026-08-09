namespace CodexUsageMonitor.App.Services;

internal static class TrayMenuInputGuard
{
    public static void ReleaseWpfMouseCapture()
    {
        _ = System.Windows.Input.Mouse.Capture(null);
    }
}
