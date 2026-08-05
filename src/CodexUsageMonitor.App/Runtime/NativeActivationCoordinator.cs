using CodexUsageMonitor.Notifications.Native;
using CodexUsageMonitor.Windows.Runtime;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.App.Runtime;

public sealed class NativeActivationCoordinator : IDisposable
{
    private readonly INativeNotificationService _notifications;
    private readonly ApplicationCommandRouter _commands;
    private readonly ApplicationLifetimeController _lifetime;
    private readonly ILogger<NativeActivationCoordinator> _logger;
    private bool _started;
    private bool _disposed;

    public NativeActivationCoordinator(
        INativeNotificationService notifications,
        ApplicationCommandRouter commands,
        ApplicationLifetimeController lifetime,
        ILogger<NativeActivationCoordinator> logger)
    {
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _notifications.Activated += OnActivated;
        _started = true;
    }

    private void OnActivated(object? sender, NativeNotificationActivation activation)
    {
        var command = new ActivationCommand(
            activation.Action,
            string.IsNullOrWhiteSpace(activation.Argument) ? null : activation.Argument);
        var message = new ActivationMessage(ActivationMessage.CurrentVersion, [command]);
        _ = RouteAsync(message);
    }

    private async Task RouteAsync(ActivationMessage message)
    {
        try
        {
            await _commands.RouteAsync(message, _lifetime.ApplicationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.ApplicationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            _logger.LogWarning(exception, "Native notification activation could not be routed.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_started)
        {
            _notifications.Activated -= OnActivated;
        }
    }
}
