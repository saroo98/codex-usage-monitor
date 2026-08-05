namespace CodexUsageMonitor.Notifications.Native;

public sealed record NativeNotificationContent(
    string Title,
    string Body,
    string? Attribution,
    bool PlaySound,
    IReadOnlyList<NativeNotificationAction> Actions);

public sealed record NativeNotificationAction(string Label, string Action, string? Argument = null);

public sealed record NativeNotificationActivation(string Action, string? Argument);

public interface INativeNotificationService : IDisposable
{
    event EventHandler<NativeNotificationActivation>? Activated;

    void Register();

    Task ShowAsync(NativeNotificationContent content, CancellationToken cancellationToken);
}
