using System.Windows;
using System.Windows.Forms;
using CodexUsageMonitor.Core.Settings;

namespace CodexUsageMonitor.Windows.Windowing;

public sealed class MonitorPlacementService
{
    public IReadOnlyList<MonitorWorkArea> GetMonitors()
    {
        var monitors = new List<MonitorWorkArea>(Screen.AllScreens.Length);
        foreach (var screen in Screen.AllScreens)
        {
            var scale = GetScaleForPoint(screen.Bounds.Left + (screen.Bounds.Width / 2), screen.Bounds.Top + (screen.Bounds.Height / 2));
            monitors.Add(new MonitorWorkArea(
                screen.DeviceName,
                FromPixels(screen.Bounds, scale.X, scale.Y),
                FromPixels(screen.WorkingArea, scale.X, scale.Y),
                scale.X,
                scale.Y,
                screen.Primary));
        }

        return monitors;
    }

    public MonitorWorkArea Resolve(WidgetPlacement? saved, DipRect desired)
    {
        var monitors = GetMonitors();
        if (monitors.Count == 0)
        {
            return new MonitorWorkArea("DISPLAY", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), 1, 1, true);
        }

        if (saved is not null)
        {
            var named = monitors.FirstOrDefault(monitor =>
                string.Equals(monitor.DeviceName, saved.MonitorDeviceName, StringComparison.OrdinalIgnoreCase));
            if (named is not null)
            {
                return named;
            }
        }

        var centerX = desired.Left + (desired.Width / 2);
        var centerY = desired.Top + (desired.Height / 2);
        return monitors
            .OrderBy(monitor => DistanceSquared(centerX, centerY, monitor.WorkAreaDip))
            .FirstOrDefault()
            ?? monitors.First(monitor => monitor.IsPrimary);
    }

    public DipRect Restore(
        WidgetPlacement? placement,
        double widthDip,
        double heightDip,
        bool snap,
        bool allowTaskbarOverlap)
    {
        var desired = placement is null
            ? new DipRect(double.NaN, double.NaN, widthDip, heightDip)
            : new DipRect(placement.LeftDip, placement.TopDip, widthDip, heightDip);
        var primary = GetMonitors().FirstOrDefault(static monitor => monitor.IsPrimary)
            ?? new MonitorWorkArea("DISPLAY", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), 1, 1, true);
        var primaryArea = SelectPlacementArea(primary, allowTaskbarOverlap);
        if (!double.IsFinite(desired.Left) || !double.IsFinite(desired.Top))
        {
            desired = desired.WithPosition(primaryArea.Right - widthDip - EdgeSnapper.DefaultGapDip,
                primaryArea.Bottom - heightDip - EdgeSnapper.DefaultGapDip);
        }

        var monitor = Resolve(placement, desired);
        return EdgeSnapper.ClampAndSnap(desired, SelectPlacementArea(monitor, allowTaskbarOverlap), snap);
    }

    public static DipRect SelectPlacementArea(MonitorWorkArea monitor, bool allowTaskbarOverlap)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        return allowTaskbarOverlap ? monitor.BoundsDip : monitor.WorkAreaDip;
    }

    public WidgetPlacement Capture(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var rect = new DipRect(window.Left, window.Top, window.ActualWidth, window.ActualHeight);
        var monitor = Resolve(null, rect);
        return new WidgetPlacement(
            monitor.DeviceName,
            rect.Left,
            rect.Top,
            rect.Width,
            rect.Height,
            monitor.DpiScaleX,
            monitor.DpiScaleY,
            DateTimeOffset.UtcNow);
    }

    private static DipRect FromPixels(System.Drawing.Rectangle rectangle, double scaleX, double scaleY) =>
        new(rectangle.Left / scaleX, rectangle.Top / scaleY, rectangle.Width / scaleX, rectangle.Height / scaleY);

    private static double DistanceSquared(double x, double y, DipRect rect)
    {
        var nearestX = Math.Clamp(x, rect.Left, rect.Right);
        var nearestY = Math.Clamp(y, rect.Top, rect.Bottom);
        return Math.Pow(x - nearestX, 2) + Math.Pow(y - nearestY, 2);
    }

    private static (double X, double Y) GetScaleForPoint(int x, int y)
    {
        var point = new NativeMethods.Point(x, y);
        var monitor = NativeMethods.MonitorFromPoint(point, NativeMethods.MonitorDefaultToNearest);
        if (monitor != nint.Zero && NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MdtEffectiveDpi, out var dpiX, out var dpiY) == 0)
        {
            return (Math.Max(1, dpiX / 96d), Math.Max(1, dpiY / 96d));
        }

        return (1, 1);
    }

    private static class NativeMethods
    {
        internal const uint MonitorDefaultToNearest = 2;
        internal const int MdtEffectiveDpi = 0;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal readonly struct Point(int x, int y)
        {
            internal readonly int X = x;
            internal readonly int Y = y;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern nint MonitorFromPoint(Point point, uint flags);

        [System.Runtime.InteropServices.DllImport("shcore.dll")]
        internal static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);
    }
}
