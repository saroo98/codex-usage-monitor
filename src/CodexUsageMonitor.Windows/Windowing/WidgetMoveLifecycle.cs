namespace CodexUsageMonitor.Windows.Windowing;

/// <summary>
/// Coordinates work-area requests with the pointer-owned widget drag session.
/// </summary>
public sealed class WidgetMoveLifecycle
{
    private bool _userDragActive;
    private bool _deferredClamp;

    public bool IsUserMoveActive => _userDragActive;
    public bool HasDeferredClamp => _deferredClamp;

    public void BeginUserMove()
    {
        if (_userDragActive)
        {
            return;
        }

        _userDragActive = true;
        _deferredClamp = false;
    }

    public bool RequestExternalClamp(Action clamp)
    {
        ArgumentNullException.ThrowIfNull(clamp);
        if (_userDragActive)
        {
            _deferredClamp = true;
            return false;
        }

        clamp();
        return true;
    }

    public bool CompleteUserMove(Action clamp)
    {
        ArgumentNullException.ThrowIfNull(clamp);
        if (!_userDragActive)
        {
            return false;
        }

        _userDragActive = false;
        _deferredClamp = false;
        clamp();
        return true;
    }

    public void CancelUserMove()
    {
        _userDragActive = false;
        _deferredClamp = false;
    }
}
