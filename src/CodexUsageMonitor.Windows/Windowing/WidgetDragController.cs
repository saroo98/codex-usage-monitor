using System.Windows;
using System.Windows.Input;

namespace CodexUsageMonitor.Windows.Windowing;

public sealed class WidgetDragController : IDisposable
{
    private readonly Window _window;
    private readonly Func<bool> _isLocked;
    private readonly Func<bool> _snapEnabled;
    private readonly MonitorPlacementService _placements;
    private bool _disposed;

    public WidgetDragController(
        Window window,
        Func<bool> isLocked,
        Func<bool> snapEnabled,
        MonitorPlacementService placements)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _isLocked = isLocked ?? throw new ArgumentNullException(nameof(isLocked));
        _snapEnabled = snapEnabled ?? throw new ArgumentNullException(nameof(snapEnabled));
        _placements = placements ?? throw new ArgumentNullException(nameof(placements));
        _window.PreviewMouseLeftButtonDown += OnMouseDown;
    }

    public event EventHandler? Clicked;
    public event EventHandler? PlacementChanged;

    private void OnMouseDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (_isLocked())
        {
            return;
        }

        var originalLeft = _window.Left;
        var originalTop = _window.Top;
        try
        {
            _window.DragMove();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (Math.Abs(_window.Left - originalLeft) >= 0.5 || Math.Abs(_window.Top - originalTop) >= 0.5)
        {
            var rect = new DipRect(_window.Left, _window.Top, _window.ActualWidth, _window.ActualHeight);
            var monitor = _placements.Resolve(null, rect);
            var final = EdgeSnapper.ClampAndSnap(rect, monitor.WorkAreaDip, _snapEnabled());
            _window.Left = final.Left;
            _window.Top = final.Top;
            PlacementChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            Clicked?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.PreviewMouseLeftButtonDown -= OnMouseDown;
    }
}
