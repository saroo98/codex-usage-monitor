using Microsoft.Win32;

namespace CodexUsageMonitor.Windows.SystemEvents;

public enum AppSystemEventKind
{
    Resume,
    Suspend,
    SessionLocked,
    SessionUnlocked,
    DisplayChanged,
    TimeChanged,
    UserPreferenceChanged,
}

public sealed record AppSystemEvent(AppSystemEventKind Kind, DateTimeOffset ObservedAtUtc);

public sealed class WindowsSystemEventSource : IDisposable
{
    private readonly SynchronizationContext? _context;
    private bool _started;
    private bool _disposed;

    public WindowsSystemEventSource(SynchronizationContext? context = null)
    {
        _context = context ?? SynchronizationContext.Current;
    }

    public event EventHandler<AppSystemEvent>? Raised;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _started = true;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Microsoft.Win32.SystemEvents.TimeChanged += OnTimeChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs eventArgs)
    {
        if (eventArgs.Mode is PowerModes.Resume)
        {
            Publish(AppSystemEventKind.Resume);
        }
        else if (eventArgs.Mode is PowerModes.Suspend)
        {
            Publish(AppSystemEventKind.Suspend);
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs eventArgs)
    {
        if (eventArgs.Reason is SessionSwitchReason.SessionLock)
        {
            Publish(AppSystemEventKind.SessionLocked);
        }
        else if (eventArgs.Reason is SessionSwitchReason.SessionUnlock)
        {
            Publish(AppSystemEventKind.SessionUnlocked);
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs eventArgs) => Publish(AppSystemEventKind.DisplayChanged);

    private void OnTimeChanged(object? sender, EventArgs eventArgs) => Publish(AppSystemEventKind.TimeChanged);

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs eventArgs) =>
        Publish(AppSystemEventKind.UserPreferenceChanged);

    private void Publish(AppSystemEventKind kind)
    {
        var eventArgs = new AppSystemEvent(kind, DateTimeOffset.UtcNow);
        if (_context is null)
        {
            Raised?.Invoke(this, eventArgs);
            return;
        }

        _context.Post(static state =>
        {
            var (source, args) = ((WindowsSystemEventSource, AppSystemEvent))state!;
            source.Raised?.Invoke(source, args);
        }, (this, eventArgs));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_started)
        {
            return;
        }

        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        Microsoft.Win32.SystemEvents.TimeChanged -= OnTimeChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }
}
