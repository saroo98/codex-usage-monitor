using System.Diagnostics;

namespace CodexUsageMonitor.Email.OAuth;

public interface IBrowserLauncher
{
    Task OpenAsync(Uri uri, CancellationToken cancellationToken);
}

public sealed class SystemBrowserLauncher : IBrowserLauncher
{
    public Task OpenAsync(Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        cancellationToken.ThrowIfCancellationRequested();
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}
