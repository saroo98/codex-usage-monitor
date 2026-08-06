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
    private bool _allowClose;
    private bool _loaded;
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
        _dragController = new WidgetDragController(
            this,
            () => _settings.Current.Widget.Locked,
            () => _settings.Current.Widget.SnapToEdges,
            _placements);
        _dragController.PlacementChanged += OnPlacementChanged;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        _settings.Changed += OnSettingsChanged;
        MouseEnter += OnMouseEnter;
        MouseLeave += OnMouseLeave;
        IsVisibleChanged += OnIsVisibleChanged;
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
        Opacity = settings.Widget.Opacity;
        Topmost = settings.Widget.Topmost;
        _interop.SetClickThrough(settings.Widget.ClickThrough);
        ApplyVisualSize(settings.Widget.Size);

        if (restorePlacement && _loaded)
        {
            var restored = _placements.Restore(settings.Widget.Placement, _viewModel.Width, _viewModel.Height, settings.Widget.SnapToEdges);
            Left = restored.Left;
            Top = restored.Top;
        }
    }

    private void ApplyVisualSize(WidgetSize size)
    {
        (Width, Height) = size switch
        {
            WidgetSize.Medium => (208d, 60d),
            WidgetSize.Small => (148d, 42d),
            _ => (104d, 30d),
        };
        MediumLayout.Visibility = size is WidgetSize.Medium ? Visibility.Visible : Visibility.Collapsed;
        SmallLayout.Visibility = size is WidgetSize.Small ? Visibility.Visible : Visibility.Collapsed;
        ExtraSmallLayout.Visibility = size is WidgetSize.ExtraSmall ? Visibility.Visible : Visibility.Collapsed;
        WidgetChrome.CornerRadius = size switch
        {
            WidgetSize.Medium => new CornerRadius(14),
            WidgetSize.Small => new CornerRadius(11),
            _ => new CornerRadius(9),
        };
    }

    private void OnSettingsChanged(object? sender, AppSettings settings) =>
        Dispatcher.InvokeAsync(() => ApplySettings(settings, restorePlacement: true));

    private async void OnPlacementChanged(object? sender, EventArgs eventArgs)
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
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _dragController.PlacementChanged -= OnPlacementChanged;
        _dragController.Dispose();
        _interop.RecoveryRequested -= OnRecoveryRequested;
        _interop.Dispose();
        GC.SuppressFinalize(this);
    }
}
