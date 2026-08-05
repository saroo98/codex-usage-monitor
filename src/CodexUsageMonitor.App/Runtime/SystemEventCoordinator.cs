using CodexUsageMonitor.App.Services;
using CodexUsageMonitor.Codex.Monitoring;
using CodexUsageMonitor.Email.Outbox;
using CodexUsageMonitor.Notifications.Delivery;
using CodexUsageMonitor.Windows.SystemEvents;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.App.Runtime;

public sealed class SystemEventCoordinator : IDisposable
{
    private readonly WindowsSystemEventSource _source;
    private readonly MultiProfileMonitorCoordinator _monitors;
    private readonly ApplicationSettingsService _settings;
    private readonly ThemeManager _themes;
    private readonly WindowCoordinator _windows;
    private readonly EmailOutboxSignal _emailOutboxSignal;
    private readonly DeferredNotificationSignal _notificationSignal;
    private readonly ILogger<SystemEventCoordinator> _logger;
    private bool _started;
    private bool _disposed;

    public SystemEventCoordinator(
        WindowsSystemEventSource source,
        MultiProfileMonitorCoordinator monitors,
        ApplicationSettingsService settings,
        ThemeManager themes,
        WindowCoordinator windows,
        EmailOutboxSignal emailOutboxSignal,
        DeferredNotificationSignal notificationSignal,
        ILogger<SystemEventCoordinator> logger)
    {
        _source = source;
        _monitors = monitors;
        _settings = settings;
        _themes = themes;
        _windows = windows;
        _emailOutboxSignal = emailOutboxSignal ?? throw new ArgumentNullException(nameof(emailOutboxSignal));
        _notificationSignal = notificationSignal ?? throw new ArgumentNullException(nameof(notificationSignal));
        _logger = logger;
    }

    public event EventHandler<AppSystemEvent>? Observed;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;
        _source.Raised += OnRaised;
        _source.Start();
        _started = true;
    }

    private void OnRaised(object? sender, AppSystemEvent eventArgs)
    {
        try
        {
            switch (eventArgs.Kind)
            {
                case AppSystemEventKind.Resume:
                case AppSystemEventKind.SessionUnlocked:
                case AppSystemEventKind.TimeChanged:
                    _monitors.RequestRefreshAll(manual: false);
                    _emailOutboxSignal.Pulse(EmailOutboxWakeReason.SystemResumed);
                    _notificationSignal.Pulse();
                    break;
                case AppSystemEventKind.DisplayChanged:
                    _windows.ReflowWidget();
                    break;
                case AppSystemEventKind.UserPreferenceChanged:
                    _themes.Apply(_settings.Current.Widget.Theme);
                    _windows.ReflowWidget();
                    break;
            }

            Observed?.Invoke(this, eventArgs);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            _logger.LogWarning(exception, "System event {SystemEvent} could not be fully applied.", eventArgs.Kind);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_started) _source.Raised -= OnRaised;
        _source.Dispose();
    }
}
