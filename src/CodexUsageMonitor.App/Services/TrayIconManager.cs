using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using CodexUsageMonitor.App.ViewModels;
using CodexUsageMonitor.Codex.Monitoring;
using CodexUsageMonitor.Core.Settings;
using CodexUsageMonitor.Windows.Startup;

namespace CodexUsageMonitor.App.Services;

public sealed record TrayActions(Action ShowWidget, Action HideWidget, Action OpenSettings, Action Exit);

public sealed class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly WidgetViewModel _widget;
    private readonly UsageApplicationState _applicationState;
    private readonly ApplicationSettingsService _settings;
    private readonly MultiProfileMonitorCoordinator _monitors;
    private readonly IStartupRegistration _startup;
    private readonly TrayActions _actions;
    private readonly ToolStripMenuItem _showItem;
    private readonly ToolStripMenuItem _profileMenu;
    private readonly ToolStripMenuItem _sizeMenu;
    private readonly ToolStripMenuItem _lockItem;
    private readonly ToolStripMenuItem _clickThroughItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly Icon _applicationIcon;
    private Icon? _stateIcon;
    private bool _widgetVisible = true;
    private bool _disposed;

    public TrayIconManager(
        WidgetViewModel widget,
        UsageApplicationState applicationState,
        ApplicationSettingsService settings,
        MultiProfileMonitorCoordinator monitors,
        IStartupRegistration startup,
        TrayActions actions)
    {
        _widget = widget ?? throw new ArgumentNullException(nameof(widget));
        _applicationState = applicationState ?? throw new ArgumentNullException(nameof(applicationState));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _monitors = monitors ?? throw new ArgumentNullException(nameof(monitors));
        _startup = startup ?? throw new ArgumentNullException(nameof(startup));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _showItem = new ToolStripMenuItem("Hide widget", null, (_, _) => ToggleWidget());
        _profileMenu = new ToolStripMenuItem("Active profile");
        _sizeMenu = new ToolStripMenuItem("Widget size");
        _lockItem = new ToolStripMenuItem("Lock position", null, async (_, _) => await ToggleLockAsync()) { CheckOnClick = false };
        _clickThroughItem = new ToolStripMenuItem("Click-through", null, async (_, _) => await ToggleClickThroughAsync()) { CheckOnClick = false };
        _startupItem = new ToolStripMenuItem("Start with Windows", null, async (_, _) => await ToggleStartupAsync()) { CheckOnClick = false };
        _menu = BuildMenu();
        _menu.Opening += OnMenuOpening;
        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowWidget();
        _notifyIcon.MouseClick += OnNotifyIconMouseClick;
        _widget.PropertyChanged += OnWidgetPropertyChanged;
        _settings.Changed += OnSettingsChanged;
        _applicationIcon = LoadApplicationIcon();
        UpdateIcon();
    }

    public void SetWidgetVisible(bool visible)
    {
        _widgetVisible = visible;
        _showItem.Text = visible ? "Hide widget" : "Show widget";
    }

    public void RefreshFromState()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        UpdateIcon();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(_showItem);
        menu.Items.Add(new ToolStripMenuItem("Refresh now", null, (_, _) => Refresh()) { ShortcutKeyDisplayString = "F5" });
        menu.Items.Add(_profileMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_sizeMenu);
        menu.Items.Add(_lockItem);
        menu.Items.Add(_clickThroughItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => _actions.OpenSettings()));
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => _actions.Exit()));
        return menu;
    }

    private async void OnMenuOpening(object? sender, CancelEventArgs eventArgs)
    {
        RebuildProfiles();
        RebuildSizes();
        var settings = _settings.Current;
        _lockItem.Checked = settings.Widget.Locked;
        _clickThroughItem.Checked = settings.Widget.ClickThrough;
        var startup = await _startup.GetStateAsync(CancellationToken.None);
        _startupItem.Checked = startup.IsEnabled;
        _startupItem.Enabled = startup.CanChange;
        _startupItem.ToolTipText = startup.SafeReasonCode ?? string.Empty;
    }

    private void RebuildProfiles()
    {
        _profileMenu.DropDownItems.Clear();
        foreach (var profile in _settings.Current.Profiles.Where(static profile => profile.Enabled))
        {
            var item = new ToolStripMenuItem(profile.Name)
            {
                Checked = _applicationState.ActiveProfileId == profile.Id,
                Tag = profile.Id,
            };
            item.Click += (_, _) =>
            {
                if (item.Tag is Guid id) _applicationState.SetActiveProfile(id);
            };
            _profileMenu.DropDownItems.Add(item);
        }

        _profileMenu.Enabled = _profileMenu.DropDownItems.Count > 1;
    }

    private void RebuildSizes()
    {
        _sizeMenu.DropDownItems.Clear();
        foreach (var size in Enum.GetValues<WidgetSize>())
        {
            var label = size switch
            {
                WidgetSize.ExtraSmall => "Extra Small",
                WidgetSize.XXS => "XXS (Square)",
                _ => size.ToString(),
            };
            var item = new ToolStripMenuItem(label)
            {
                Checked = _settings.Current.Widget.Size == size,
                Tag = size,
            };
            item.Click += async (_, _) =>
            {
                if (item.Tag is WidgetSize selected)
                {
                    await _settings.UpdateAsync(current => current with
                    {
                        Widget = current.Widget with { Size = selected },
                    }, CancellationToken.None);
                }
            };
            _sizeMenu.DropDownItems.Add(item);
        }
    }

    private void OnNotifyIconMouseClick(object? sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.Button is MouseButtons.Left)
        {
            ShowWidget();
        }
    }

    private void ToggleWidget()
    {
        if (_widgetVisible)
        {
            _actions.HideWidget();
            SetWidgetVisible(false);
        }
        else
        {
            ShowWidget();
        }
    }

    private void ShowWidget()
    {
        _actions.ShowWidget();
        SetWidgetVisible(true);
    }

    private void Refresh()
    {
        if (_applicationState.ActiveProfileId is { } id) _monitors.RequestRefresh(id);
        else _monitors.RequestRefreshAll();
    }

    private async Task ToggleLockAsync() =>
        await _settings.UpdateAsync(current => current with
        {
            Widget = current.Widget with { Locked = !current.Widget.Locked },
        }, CancellationToken.None);

    private async Task ToggleClickThroughAsync() =>
        await _settings.UpdateAsync(current => current with
        {
            Widget = current.Widget with { ClickThrough = !current.Widget.ClickThrough },
        }, CancellationToken.None);

    private async Task ToggleStartupAsync()
    {
        var current = await _startup.GetStateAsync(CancellationToken.None);
        var updated = await _startup.SetEnabledAsync(!current.IsEnabled, CancellationToken.None);
        await _settings.UpdateAsync(settings => settings with
        {
            General = settings.General with { StartWithWindows = updated.IsEnabled },
        }, CancellationToken.None);
    }

    private void OnWidgetPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(WidgetViewModel.VisualState) or nameof(WidgetViewModel.RemainingText) or nameof(WidgetViewModel.LimitLabel))
        {
            UpdateIcon();
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings) => UpdateIcon();

    private void UpdateIcon()
    {
        _stateIcon?.Dispose();
        _stateIcon = TrayIconRenderer.Create(_applicationIcon, _widget.VisualState);
        _notifyIcon.Icon = _stateIcon;
        var text = $"Codex: {_widget.RemainingText} {_widget.LimitLabel}";
        _notifyIcon.Text = text[..Math.Min(text.Length, 63)];
    }

    private static Icon LoadApplicationIcon()
    {
        if (Environment.ProcessPath is { } executablePath)
        {
            using var extracted = Icon.ExtractAssociatedIcon(executablePath);
            if (extracted is not null)
            {
                return (Icon)extracted.Clone();
            }
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _widget.PropertyChanged -= OnWidgetPropertyChanged;
        _settings.Changed -= OnSettingsChanged;
        _menu.Opening -= OnMenuOpening;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _stateIcon?.Dispose();
        _applicationIcon.Dispose();
    }
}
