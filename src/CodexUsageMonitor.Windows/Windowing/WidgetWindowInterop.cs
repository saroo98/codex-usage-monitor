using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CodexUsageMonitor.Windows.Windowing;

public sealed class WidgetWindowInterop : IDisposable
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const int WmHotKey = 0x0312;
    private const int RecoveryHotKeyId = 0x43554D;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkU = 0x55;

    private readonly Window _window;
    private HwndSource? _source;
    private nint _handle;
    private bool _hotKeyRegistered;
    private bool _disposed;

    public WidgetWindowInterop(Window window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _window.SourceInitialized += OnSourceInitialized;
        _window.Closed += OnClosed;
    }

    public event EventHandler? RecoveryRequested;

    public bool IsClickThrough { get; private set; }

    public void SetClickThrough(bool enabled)
    {
        ThrowIfDisposed();
        IsClickThrough = enabled;
        if (_handle == nint.Zero)
        {
            return;
        }

        var style = GetExtendedStyle(_handle);
        style |= WsExToolWindow;
        if (enabled)
        {
            style |= WsExTransparent | WsExNoActivate;
        }
        else
        {
            style &= ~(WsExTransparent | WsExNoActivate);
        }

        SetExtendedStyle(_handle, style);
    }

    public void BringToForeground()
    {
        ThrowIfDisposed();
        if (_handle == nint.Zero)
        {
            return;
        }

        _window.Show();
        _window.WindowState = WindowState.Normal;
        NativeMethods.ShowWindow(_handle, NativeMethods.SwShowNoActivate);
        NativeMethods.SetWindowPos(
            _handle,
            NativeMethods.HwndTopmost,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
    }

    private void OnSourceInitialized(object? sender, EventArgs eventArgs)
    {
        _handle = new WindowInteropHelper(_window).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WndProc);
        SetClickThrough(IsClickThrough);
        _hotKeyRegistered = NativeMethods.RegisterHotKey(
            _handle,
            RecoveryHotKeyId,
            ModControl | ModShift | ModNoRepeat,
            VkU);
        if (!_hotKeyRegistered)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register the click-through recovery shortcut Ctrl+Shift+U.");
        }
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmHotKey && wParam == RecoveryHotKeyId)
        {
            handled = true;
            if (IsClickThrough)
            {
                SetClickThrough(false);
            }

            RecoveryRequested?.Invoke(this, EventArgs.Empty);
        }

        return nint.Zero;
    }

    private void OnClosed(object? sender, EventArgs eventArgs) => Dispose();

    private static long GetExtendedStyle(nint handle)
    {
        Marshal.SetLastPInvokeError(0);
        var value = NativeMethods.GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        var error = Marshal.GetLastPInvokeError();
        if (value == 0 && error != 0)
        {
            throw new Win32Exception(error);
        }

        return value;
    }

    private static void SetExtendedStyle(nint handle, long style)
    {
        Marshal.SetLastPInvokeError(0);
        var previous = NativeMethods.SetWindowLongPtr(handle, GwlExStyle, new nint(style));
        var error = Marshal.GetLastPInvokeError();
        if (previous == nint.Zero && error != 0)
        {
            throw new Win32Exception(error);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.SourceInitialized -= OnSourceInitialized;
        _window.Closed -= OnClosed;
        if (_hotKeyRegistered && _handle != nint.Zero)
        {
            NativeMethods.UnregisterHotKey(_handle, RecoveryHotKeyId);
        }

        _source?.RemoveHook(WndProc);
        _source = null;
        _handle = nint.Zero;
    }

    private static class NativeMethods
    {
        internal const int SwShowNoActivate = 4;
        internal static readonly nint HwndTopmost = new(-1);
        internal const uint SwpNoSize = 0x0001;
        internal const uint SwpNoMove = 0x0002;
        internal const uint SwpNoActivate = 0x0010;
        internal const uint SwpShowWindow = 0x0040;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        internal static extern nint GetWindowLongPtr(nint window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        internal static extern nint SetWindowLongPtr(nint window, int index, nint newLong);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterHotKey(nint window, int id);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(nint window, int command);

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
