using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using CodexUsageMonitor.App.Runtime;
using CodexUsageMonitor.App.Services;
using CodexUsageMonitor.App.ViewModels;
using CodexUsageMonitor.Core.Settings;
using CodexUsageMonitor.Windows.Windowing;

namespace CodexUsageMonitor.App.Views;

public partial class WidgetWindow : Window, IDisposable, IWidgetWindow
{
    private readonly WidgetViewModel _viewModel = null!;
    private readonly ApplicationSettingsService _settings = null!;
    private readonly MonitorPlacementService _placements = null!;
    private readonly WidgetWindowInterop _interop = null!;
    private readonly WidgetDragController _dragController = null!;
    private readonly WidgetMoveLifecycle _moveLifecycle = new();
    private bool _allowClose;
    private bool _loaded;
    private bool _clampInProgress;
    private bool _applyingSettings;
    private bool _disposed;

    private WidgetWindow()
    {
        InitializeComponent();
    }

    public WidgetWindow(
        WidgetViewModel viewModel,
        ApplicationSettingsService settings,
        MonitorPlacementService placements) : this()
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _placements = placements ?? throw new ArgumentNullException(nameof(placements));
        DataContext = _viewModel;
        ToolTipService.SetInitialShowDelay(WidgetChrome, 400);
        ToolTipService.SetBetweenShowDelay(WidgetChrome, 200);
        ToolTipService.SetShowDuration(WidgetChrome, 30_000);
        _interop = new WidgetWindowInterop(this);
        _interop.RecoveryRequested += OnRecoveryRequested;
        _interop.WorkAreaChanged += OnWorkAreaChanged;
        _dragController = new WidgetDragController(
            this,
            () => _settings.Current.Widget.Locked,
            () => _interop.IsClickThrough);
        _dragController.DragStarted += OnDragStarted;
        _dragController.DragCompleted += OnDragCompleted;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        _settings.Changed += OnSettingsChanged;
        MouseEnter += OnMouseEnter;
        MouseLeave += OnMouseLeave;
        IsVisibleChanged += OnIsVisibleChanged;
        SizeChanged += OnSizeChanged;
        DpiChanged += OnDpiChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ApplySettings(_settings.Current, restorePlacement: false);
    }

    internal static WidgetWindow CreateVisualEvidenceWindow(object dataContext, WidgetSize size)
    {
        ArgumentNullException.ThrowIfNull(dataContext);
        var window = new WidgetWindow { DataContext = dataContext };
        window.ApplyVisualSize(size);
        return window;
    }

    internal FrameworkElement VisualEvidenceSurface => WidgetChrome;
    Window IWidgetWindow.OwnerWindow => this;

    public void ShowWithoutActivation()
    {
        Show();
        RequestClamp(snap: false);
        _interop.BringToForeground();
    }

    public void RestorePlacement() => ApplySettings(_settings.Current, restorePlacement: true);

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        _loaded = true;
        ApplySettings(_settings.Current, restorePlacement: true);
    }

    private void ApplySettings(AppSettings settings, bool restorePlacement)
    {
        _applyingSettings = true;
        try
        {
            Opacity = settings.Widget.Opacity;
            Topmost = settings.Widget.Topmost;
            _interop.SetClickThrough(settings.Widget.ClickThrough);
            ApplyVisualSize(settings.Widget.Size);

            if (restorePlacement && _loaded && !_dragController.IsDragging && !_moveLifecycle.IsUserMoveActive)
            {
                _placements.Restore(
                    this,
                    settings.Widget.Placement,
                    settings.Widget.AllowTaskbarOverlap);
            }
        }
        finally
        {
            _applyingSettings = false;
        }
    }

    private void ApplyVisualSize(WidgetSize size)
    {
        (Width, Height) = size switch
        {
            WidgetSize.Medium => (208d, 60d),
            WidgetSize.Small => (148d, 42d),
            WidgetSize.ExtraSmall => (104d, 30d),
            WidgetSize.XXS => (48d, 48d),
            _ => (208d, 60d),
        };
        MediumLayout.Visibility = size is WidgetSize.Medium ? Visibility.Visible : Visibility.Collapsed;
        SmallLayout.Visibility = size is WidgetSize.Small ? Visibility.Visible : Visibility.Collapsed;
        ExtraSmallLayout.Visibility = size is WidgetSize.ExtraSmall ? Visibility.Visible : Visibility.Collapsed;
        XXSLayout.Visibility = size is WidgetSize.XXS ? Visibility.Visible : Visibility.Collapsed;
        WidgetChrome.CornerRadius = size switch
        {
            WidgetSize.Medium => new CornerRadius(14),
            WidgetSize.Small => new CornerRadius(11),
            WidgetSize.ExtraSmall => new CornerRadius(9),
            WidgetSize.XXS => new CornerRadius(12),
            _ => new CornerRadius(14),
        };
    }

    private void OnSettingsChanged(object? sender, AppSettings settings) =>
        Dispatcher.InvokeAsync(() =>
        {
            if (!_disposed)
            {
                ApplySettings(settings, restorePlacement: true);
            }
        });

    private void OnSizeChanged(object sender, SizeChangedEventArgs eventArgs) => RequestClamp(snap: false);

    private void OnDpiChanged(object sender, System.Windows.DpiChangedEventArgs eventArgs) => RequestClamp(snap: false);

    private void OnWorkAreaChanged(object? sender, EventArgs eventArgs) => RequestClamp(snap: false);

    private void OnDragStarted(object? sender, EventArgs eventArgs) => _moveLifecycle.BeginUserMove();

    private void OnDragCompleted(object? sender, EventArgs eventArgs)
    {
        if (_moveLifecycle.CompleteUserMove(CompleteUserMove))
        {
            return;
        }

        CompleteUserMove();
    }

    private void RequestClamp(bool snap)
    {
        if (!_loaded || _disposed || _applyingSettings || _clampInProgress)
        {
            return;
        }

        _moveLifecycle.RequestExternalClamp(() => ClampWindow(snap));
    }

    private void ClampWindow(bool snap)
    {
        if (_disposed || _clampInProgress)
        {
            return;
        }

        _clampInProgress = true;
        try
        {
            _placements.ClampWindow(
                this,
                snap,
                _settings.Current.Widget.AllowTaskbarOverlap);
        }
        finally
        {
            _clampInProgress = false;
        }
    }

    private void CompleteUserMove()
    {
        if (_disposed || !_loaded)
        {
            return;
        }

        ClampWindow(_settings.Current.Widget.SnapToEdges);
        _ = PersistPlacementAsync();
    }

    private async Task PersistPlacementAsync()
    {
        var placement = _placements.Capture(this);
        await _settings.UpdateAsync(current => current with
        {
            Widget = current.Widget with { Placement = placement },
        }, CancellationToken.None);
    }

    private async void OnRecoveryRequested(object? sender, EventArgs eventArgs)
    {
        await _settings.UpdateAsync(current => current with
        {
            Widget = current.Widget with { ClickThrough = false },
        }, CancellationToken.None);
        ShowWithoutActivation();
    }

    private void OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs eventArgs) =>
        _viewModel.SetPresentationState(IsVisible, isHovering: true);

    private void OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs eventArgs) =>
        _viewModel.SetPresentationState(IsVisible, isHovering: false);

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs eventArgs) =>
        _viewModel.SetPresentationState(IsVisible, IsMouseOver);

    private void OnContextMenuOpened(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is ContextMenu menu)
        {
            menu.DataContext = DataContext;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName != nameof(WidgetViewModel.ToolTipText)
            || !AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
        {
            return;
        }

        Dispatcher.InvokeAsync(() =>
            (UIElementAutomationPeer.FromElement(this) ?? UIElementAutomationPeer.CreatePeerForElement(this))
                ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged));
    }

    private void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (!_allowClose && _settings.Current.General.CloseToTray)
        {
            eventArgs.Cancel = true;
            Hide();
        }
    }

    private void OnClosed(object? sender, EventArgs eventArgs) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Loaded -= OnLoaded;
        Closing -= OnClosing;
        Closed -= OnClosed;
        _settings.Changed -= OnSettingsChanged;
        MouseEnter -= OnMouseEnter;
        MouseLeave -= OnMouseLeave;
        IsVisibleChanged -= OnIsVisibleChanged;
        SizeChanged -= OnSizeChanged;
        DpiChanged -= OnDpiChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _dragController.DragStarted -= OnDragStarted;
        _dragController.DragCompleted -= OnDragCompleted;
        _dragController.Dispose();
        _interop.RecoveryRequested -= OnRecoveryRequested;
        _interop.WorkAreaChanged -= OnWorkAreaChanged;
        _moveLifecycle.CancelUserMove();
        _interop.Dispose();
        GC.SuppressFinalize(this);
    }
}
