using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfMouseEventHandler = System.Windows.Input.MouseEventHandler;

namespace CodexUsageMonitor.Windows.Windowing;

public sealed class WidgetDragController : IDisposable
{
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private readonly Window _window;
    private readonly Func<bool> _isLocked;
    private readonly Func<bool> _isClickThrough;
    private readonly MouseButtonEventHandler _mouseDownHandler;
    private readonly WpfMouseEventHandler _mouseMoveHandler;
    private readonly MouseButtonEventHandler _mouseUpHandler;
    private readonly WpfMouseEventHandler _lostMouseCaptureHandler;
    private readonly WidgetDragSession _dragSession = new();
    private nint _handle;
    private bool _disposed;

    public WidgetDragController(
        Window window,
        Func<bool> isLocked,
        Func<bool>? isClickThrough = null)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _isLocked = isLocked ?? throw new ArgumentNullException(nameof(isLocked));
        _isClickThrough = isClickThrough ?? (() => false);
        _mouseDownHandler = OnMouseDown;
        _mouseMoveHandler = OnMouseMove;
        _mouseUpHandler = OnMouseUp;
        _lostMouseCaptureHandler = OnLostMouseCapture;
        _window.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, _mouseDownHandler, handledEventsToo: true);
        _window.AddHandler(UIElement.PreviewMouseMoveEvent, _mouseMoveHandler, handledEventsToo: true);
        _window.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, _mouseUpHandler, handledEventsToo: true);
        _window.AddHandler(UIElement.LostMouseCaptureEvent, _lostMouseCaptureHandler, handledEventsToo: true);
    }

    public bool IsDragging => _dragSession.IsActive;

    public event EventHandler? DragStarted;
    public event EventHandler? Clicked;
    public event EventHandler? DragCompleted;

    private void OnMouseDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (_disposed || _dragSession.IsActive || eventArgs.ChangedButton != MouseButton.Left || _isLocked() || _isClickThrough())
        {
            return;
        }

        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == nint.Zero ||
            !NativeMethods.GetCursorPos(out var cursor) ||
            !NativeMethods.GetWindowRect(handle, out var windowRect))
        {
            return;
        }

        if (!_window.CaptureMouse() || !ReferenceEquals(Mouse.Captured, _window))
        {
            return;
        }

        _handle = handle;
        _dragSession.Begin(
            new WidgetDragPoint(cursor.X, cursor.Y),
            new PixelRect(windowRect.Left, windowRect.Top, windowRect.Right - windowRect.Left, windowRect.Bottom - windowRect.Top));
        DragStarted?.Invoke(this, EventArgs.Empty);
        eventArgs.Handled = true;
    }

    private void OnMouseMove(object sender, WpfMouseEventArgs eventArgs)
    {
        if (!_dragSession.IsActive)
        {
            return;
        }

        if (eventArgs.LeftButton != MouseButtonState.Pressed || !ReferenceEquals(Mouse.Captured, _window))
        {
            FinishDrag();
            return;
        }

        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        if (!_dragSession.TryMove(new WidgetDragPoint(cursor.X, cursor.Y), out var left, out var top))
        {
            return;
        }

        if (!NativeMethods.SetWindowPos(
                _handle,
                nint.Zero,
                left,
                top,
                0,
                0,
                SwpNoSize | SwpNoZOrder | SwpNoActivate))
        {
            return;
        }

        eventArgs.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs eventArgs)
    {
        if (!_dragSession.IsActive || eventArgs.ChangedButton != MouseButton.Left)
        {
            return;
        }

        FinishDrag();
        eventArgs.Handled = true;
    }

    private void OnLostMouseCapture(object sender, WpfMouseEventArgs eventArgs)
    {
        if (_dragSession.IsActive)
        {
            FinishDrag();
        }
    }

    private void FinishDrag()
    {
        if (!_dragSession.IsActive)
        {
            return;
        }

        var moved = _dragSession.Complete();
        _handle = nint.Zero;
        if (ReferenceEquals(Mouse.Captured, _window))
        {
            _window.ReleaseMouseCapture();
        }

        if (moved)
        {
            DragCompleted?.Invoke(this, EventArgs.Empty);
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
        if (_dragSession.IsActive)
        {
            _dragSession.Cancel();
            if (ReferenceEquals(Mouse.Captured, _window))
            {
                _window.ReleaseMouseCapture();
            }
        }

        _window.RemoveHandler(UIElement.PreviewMouseLeftButtonDownEvent, _mouseDownHandler);
        _window.RemoveHandler(UIElement.PreviewMouseMoveEvent, _mouseMoveHandler);
        _window.RemoveHandler(UIElement.PreviewMouseLeftButtonUpEvent, _mouseUpHandler);
        _window.RemoveHandler(UIElement.LostMouseCaptureEvent, _lostMouseCaptureHandler);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out NativePoint point);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(nint window, out NativeRect rect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(
            nint window,
            nint insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);
    }
}
