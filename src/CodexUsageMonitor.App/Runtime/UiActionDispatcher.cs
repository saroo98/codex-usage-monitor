using CodexUsageMonitor.App.Services;
using CodexUsageMonitor.App.ViewModels;
using CodexUsageMonitor.Core.ResetCredits;

namespace CodexUsageMonitor.App.Runtime;

public sealed class UiActionDispatcher
{
    private readonly ApplicationLifetimeController _lifetime;
    private readonly object _gate = new();
    private WindowCoordinator? _windows;

    public UiActionDispatcher(ApplicationLifetimeController lifetime)
    {
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
    }

    public void Attach(WindowCoordinator windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        lock (_gate)
        {
            if (_windows is not null && !ReferenceEquals(_windows, windows))
            {
                throw new InvalidOperationException("A different window coordinator is already attached.");
            }

            _windows = windows;
        }
    }

    public WidgetActions CreateWidgetActions() => new(
        () => WithWindows(static windows => windows.OpenSettings(SettingsSection.General)),
        () => WithWindows(static windows => windows.ShowWidget()),
        _lifetime.RequestExit,
        RedeemResetCreditAsync);

    public TrayActions CreateTrayActions() => new(
        () => WithWindows(static windows => windows.ShowWidget()),
        () => WithWindows(static windows => windows.HideWidget()),
        () => WithWindows(static windows => windows.OpenSettings(SettingsSection.General)),
        _lifetime.RequestExit);

    private Task RedeemResetCreditAsync(ResetCredit credit)
    {
        WindowCoordinator? windows;
        lock (_gate)
        {
            windows = _windows;
        }

        return windows?.RedeemResetCreditAsync(credit) ?? Task.CompletedTask;
    }

    private void WithWindows(Action<WindowCoordinator> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        WindowCoordinator? windows;
        lock (_gate)
        {
            windows = _windows;
        }

        if (windows is not null)
        {
            action(windows);
        }
    }
}
