using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.Notifications.Native;

public sealed class WindowsAppNotificationService : INativeNotificationService
{
    private const int MaximumTitleLength = 96;
    private const int MaximumBodyLength = 512;
    private readonly ILogger<WindowsAppNotificationService> _logger;
    private bool _registered;
    private bool _disposed;

    public WindowsAppNotificationService(ILogger<WindowsAppNotificationService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public event EventHandler<NativeNotificationActivation>? Activated;

    public void Register()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_registered)
        {
            return;
        }

        var manager = AppNotificationManager.Default;
        manager.NotificationInvoked += OnNotificationInvoked;
        manager.Register();
        _registered = true;
    }

    public Task ShowAsync(NativeNotificationContent content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_registered)
        {
            Register();
        }

        var builder = new AppNotificationBuilder()
            .AddText(Truncate(content.Title, MaximumTitleLength))
            .AddText(Truncate(content.Body, MaximumBodyLength));
        if (!string.IsNullOrWhiteSpace(content.Attribution))
        {
            builder.AddText(Truncate(content.Attribution, MaximumTitleLength));
        }

        foreach (var action in content.Actions.Take(3))
        {
            var button = new AppNotificationButton(Truncate(action.Label, 32))
                .AddArgument("action", Truncate(action.Action, 64));
            if (!string.IsNullOrWhiteSpace(action.Argument))
            {
                button.AddArgument("value", Truncate(action.Argument, 256));
            }

            builder.AddButton(button);
        }

        if (!content.PlaySound)
        {
            builder.MuteAudio();
        }

        AppNotificationManager.Default.Show(builder.BuildNotification());
        return Task.CompletedTask;
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs eventArgs)
    {
        try
        {
            var values = eventArgs.Arguments;
            values.TryGetValue("action", out var action);
            values.TryGetValue("value", out var value);
            if (!string.IsNullOrWhiteSpace(action))
            {
                Activated?.Invoke(this, new NativeNotificationActivation(action, value));
            }
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException)
        {
            _logger.LogWarning(exception, "Ignored malformed notification activation arguments.");
        }
    }

    private static string Truncate(string value, int maximum) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim()[..Math.Min(value.Trim().Length, maximum)];

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_registered)
        {
            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked -= OnNotificationInvoked;
            manager.Unregister();
        }
    }
}
