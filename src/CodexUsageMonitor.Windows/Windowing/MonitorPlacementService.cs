using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using CodexUsageMonitor.Core.Settings;

namespace CodexUsageMonitor.Windows.Windowing;

public sealed class MonitorPlacementService
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;

    public void Restore(Window window, WidgetPlacement? placement, bool allowTaskbarOverlap = false)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero || !NativeMethods.GetWindowRect(handle, out var windowRect))
        {
            return;
        }

        var monitors = EnumerateMonitors();
        if (monitors.Count == 0)
        {
            return;
        }

        var target = placement is null
            ? monitors.FirstOrDefault(static monitor => monitor.IsPrimary) ?? monitors[0]
            : monitors.FirstOrDefault(monitor => string.Equals(
                monitor.DeviceName,
                placement.MonitorDeviceName,
                StringComparison.OrdinalIgnoreCase)) ?? ResolveNearest(
                    monitors,
                    DesiredRect(placement, windowRect),
                    allowTaskbarOverlap);
        var placementArea = SelectPlacementArea(target, allowTaskbarOverlap);
        var desired = placement is null
            ? new PixelRect(
                placementArea.Right - windowRect.Width - DipToPixels(EdgeSnapper.DefaultGapDip, target.DpiScaleX),
                placementArea.Bottom - windowRect.Height - DipToPixels(EdgeSnapper.DefaultGapDip, target.DpiScaleY),
                windowRect.Width,
                windowRect.Height)
            : DesiredRect(placement, windowRect);
        Apply(handle, ClampAndSnap(desired, target, snap: false, allowTaskbarOverlap));
    }

    public void ClampWindow(Window window, bool snap, bool allowTaskbarOverlap = false)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero || !NativeMethods.GetWindowRect(handle, out var nativeRect))
        {
            return;
        }

        var monitorHandle = NativeMethods.MonitorFromWindow(handle, MonitorDefaultToNearest);
        var monitor = ReadMonitor(monitorHandle);
        if (monitor is null)
        {
            return;
        }

        Apply(handle, ClampAndSnap(nativeRect.ToPixelRect(), monitor, snap, allowTaskbarOverlap));
    }

    public WidgetPlacement Capture(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero || !NativeMethods.GetWindowRect(handle, out var rect))
        {
            throw new InvalidOperationException("The widget window does not have a native handle.");
        }

        var monitor = ReadMonitor(NativeMethods.MonitorFromWindow(handle, MonitorDefaultToNearest))
            ?? throw new Win32Exception("The widget monitor could not be resolved.");
        return new WidgetPlacement(
            monitor.DeviceName,
            window.Left,
            window.Top,
            window.ActualWidth,
            window.ActualHeight,
            monitor.DpiScaleX,
            monitor.DpiScaleY,
            DateTimeOffset.UtcNow,
            rect.Left,
            rect.Top);
    }

    private static PixelRect DesiredRect(WidgetPlacement placement, NativeRect current)
    {
        var left = SafeCoordinate(placement.PhysicalLeft, placement.LeftDip, placement.DpiScaleX, current.Width);
        var top = SafeCoordinate(placement.PhysicalTop, placement.TopDip, placement.DpiScaleY, current.Height);
        return new PixelRect(left, top, current.Width, current.Height);
    }

    private static int SafeCoordinate(int? physical, double dip, double scale, int size)
    {
        var minimum = (double)int.MinValue + Math.Max(size, 0);
        var maximum = (double)int.MaxValue - Math.Max(size, 0);
        var value = physical ?? (double.IsFinite(dip) && double.IsFinite(scale)
            ? dip * Math.Max(scale, 1d)
            : 0d);
        return (int)Math.Round(Math.Clamp(value, minimum, maximum), MidpointRounding.AwayFromZero);
    }

    private static PixelMonitor ResolveNearest(
        IReadOnlyList<PixelMonitor> monitors,
        PixelRect desired,
        bool allowTaskbarOverlap)
    {
        var centerX = desired.Left + (desired.Width / 2d);
        var centerY = desired.Top + (desired.Height / 2d);
        return monitors.OrderBy(monitor =>
                DistanceSquared(centerX, centerY, SelectPlacementArea(monitor, allowTaskbarOverlap)))
            .First();
    }

    private static PixelRect ClampAndSnap(
        PixelRect desired,
        PixelMonitor monitor,
        bool snap,
        bool allowTaskbarOverlap)
    {
        var placementArea = SelectPlacementArea(monitor, allowTaskbarOverlap);
        var clamped = WidgetWorkAreaClamp.ClampWidgetToMonitorWorkArea(desired, placementArea);
        if (!snap)
        {
            return clamped;
        }

        var gapX = DipToPixels(EdgeSnapper.DefaultGapDip, monitor.DpiScaleX);
        var gapY = DipToPixels(EdgeSnapper.DefaultGapDip, monitor.DpiScaleY);
        var left = Snap(clamped.Left, placementArea.Left, placementArea.Right - clamped.Width, gapX);
        var top = Snap(clamped.Top, placementArea.Top, placementArea.Bottom - clamped.Height, gapY);
        return clamped with { Left = left, Top = top };
    }

    public static PixelRect SelectPlacementArea(
        PixelRect monitorBounds,
        PixelRect workArea,
        bool allowTaskbarOverlap) => allowTaskbarOverlap ? monitorBounds : workArea;

    private static PixelRect SelectPlacementArea(PixelMonitor monitor, bool allowTaskbarOverlap) =>
        SelectPlacementArea(monitor.Bounds, monitor.WorkArea, allowTaskbarOverlap);

    private static int Snap(int value, int minimum, int maximum, int gap)
    {
        if (maximum - minimum < gap * 2) return minimum;
        if (Math.Abs(value - minimum) <= gap) return minimum + gap;
        if (Math.Abs(maximum - value) <= gap) return maximum - gap;
        return value;
    }

    private static void Apply(nint handle, PixelRect rect)
    {
        if (!NativeMethods.SetWindowPos(
                handle,
                nint.Zero,
                rect.Left,
                rect.Top,
                rect.Width,
                rect.Height,
                SwpNoActivate | SwpNoZOrder))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The widget position could not be updated.");
        }
    }

    private static IReadOnlyList<PixelMonitor> EnumerateMonitors()
    {
        var monitors = new List<PixelMonitor>();
        NativeMethods.EnumDisplayMonitors(nint.Zero, nint.Zero, (handle, _, _, _) =>
        {
            if (ReadMonitor(handle) is { } monitor)
            {
                monitors.Add(monitor);
            }
            return true;
        }, nint.Zero);
        return monitors;
    }

    private static PixelMonitor? ReadMonitor(nint handle)
    {
        if (handle == nint.Zero)
        {
            return null;
        }

        var info = NativeMonitorInfo.Create();
        if (!NativeMethods.GetMonitorInfo(handle, ref info))
        {
            return null;
        }

        var dpiX = 96u;
        var dpiY = 96u;
        _ = NativeMethods.GetDpiForMonitor(handle, 0, out dpiX, out dpiY);
        return new PixelMonitor(
            info.DeviceName,
            info.Monitor.ToPixelRect(),
            info.WorkArea.ToPixelRect(),
            Math.Max(1d, dpiX / 96d),
            Math.Max(1d, dpiY / 96d),
            (info.Flags & 1) != 0);
    }

    private static int DipToPixels(double dip, double scale) =>
        checked((int)Math.Round(dip * scale, MidpointRounding.AwayFromZero));

    private static double DistanceSquared(double x, double y, PixelRect rect)
    {
        var nearestX = Math.Clamp(x, rect.Left, rect.Right);
        var nearestY = Math.Clamp(y, rect.Top, rect.Bottom);
        return Math.Pow(x - nearestX, 2) + Math.Pow(y - nearestY, 2);
    }

    private sealed record PixelMonitor(
        string DeviceName,
        PixelRect Bounds,
        PixelRect WorkArea,
        double DpiScaleX,
        double DpiScaleY,
        bool IsPrimary);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public readonly PixelRect ToPixelRect() => new(Left, Top, Width, Height);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeMonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        public static NativeMonitorInfo Create() => new()
        {
            Size = Marshal.SizeOf<NativeMonitorInfo>(),
            DeviceName = string.Empty,
        };
    }

    private static class NativeMethods
    {
        internal delegate bool MonitorEnumeration(nint monitor, nint deviceContext, nint rect, nint data);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplayMonitors(nint deviceContext, nint clipRect, MonitorEnumeration callback, nint data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfo(nint monitor, ref NativeMonitorInfo info);

        [DllImport("user32.dll")]
        internal static extern nint MonitorFromWindow(nint window, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(nint window, out NativeRect rect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("shcore.dll")]
        internal static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);
    }
}
