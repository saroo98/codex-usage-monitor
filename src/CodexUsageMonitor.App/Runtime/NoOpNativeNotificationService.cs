using CodexUsageMonitor.Notifications.Native;

namespace CodexUsageMonitor.App.Runtime;

internal sealed class NoOpNativeNotificationService : INativeNotificationService
{
    public event EventHandler<NativeNotificationActivation>? Activated
    {
        add { }
        remove { }
    }

    public void Register()
    {
    }

    public Task ShowAsync(NativeNotificationContent content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
    }
}
