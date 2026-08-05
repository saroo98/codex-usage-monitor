using System.Windows;
using System.Windows.Input;

namespace CodexUsageMonitor.Windows.Windowing;

public sealed class WidgetDragController : IDisposable
{
    private readonly Window _window;
    private readonly Func<bool> _isLocked;
    private readonly Func<bool> _snapEnabled;
    private readonly MonitorPlacementService _placements;
    private System.Windows.Point? _pressPoint;
    private bool _dragging;
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
        _window.PreviewMouseMove += OnMouseMove;
        _window.PreviewMouseLeftButtonUp += OnMouseUp;
    }

    public event EventHandler? Clicked;
    public event EventHandler? PlacementChanged;

    private void OnMouseDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (_isLocked())
        {
            return;
        }

        _pressPoint = eventArgs.GetPosition(_window);
        _dragging = false;
        _window.CaptureMouse();
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs eventArgs)
    {
        if (_pressPoint is null || eventArgs.LeftButton != MouseButtonState.Pressed || _isLocked())
        {
            return;
        }

        var current = eventArgs.GetPosition(_window);
        if (!_dragging && !EdgeSnapper.HasExceededDragThreshold(
                _pressPoint.Value.X,
                _pressPoint.Value.Y,
                current.X,
                current.Y))
        {
            return;
        }

        if (!_dragging)
        {
            _dragging = true;
            try
            {
                _window.DragMove();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs eventArgs)
    {
        if (_pressPoint is null)
        {
            return;
        }

        _window.ReleaseMouseCapture();
        if (_dragging)
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

        _pressPoint = null;
        _dragging = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.PreviewMouseLeftButtonDown -= OnMouseDown;
        _window.PreviewMouseMove -= OnMouseMove;
        _window.PreviewMouseLeftButtonUp -= OnMouseUp;
    }
}
