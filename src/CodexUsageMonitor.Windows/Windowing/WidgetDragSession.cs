namespace CodexUsageMonitor.Windows.Windowing;

/// <summary>
/// Tracks a pointer-owned widget move without applying work-area policy.
/// </summary>
public sealed class WidgetDragSession
{
    private WidgetDragPoint _anchorCursor;
    private PixelRect _anchorWindow;
    private bool _active;
    private bool _moved;

    public bool IsActive => _active;
    public bool HasMoved => _moved;

    public void Begin(WidgetDragPoint cursor, PixelRect window)
    {
        if (_active)
        {
            return;
        }

        _anchorCursor = cursor;
        _anchorWindow = window;
        _moved = false;
        _active = true;
    }

    public bool TryMove(WidgetDragPoint cursor, out int left, out int top)
    {
        if (!_active)
        {
            left = 0;
            top = 0;
            return false;
        }

        left = checked(_anchorWindow.Left + cursor.X - _anchorCursor.X);
        top = checked(_anchorWindow.Top + cursor.Y - _anchorCursor.Y);
        _moved |= left != _anchorWindow.Left || top != _anchorWindow.Top;
        return true;
    }

    public bool Complete()
    {
        if (!_active)
        {
            return false;
        }

        var moved = _moved;
        Reset();
        return moved;
    }

    public void Cancel() => Reset();

    private void Reset()
    {
        _active = false;
        _moved = false;
    }
}

public readonly record struct WidgetDragPoint(int X, int Y);
