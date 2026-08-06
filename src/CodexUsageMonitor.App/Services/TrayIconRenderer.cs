using System.Drawing;
using CodexUsageMonitor.App.ViewModels;

namespace CodexUsageMonitor.App.Services;

public static class TrayIconRenderer
{
    public static Icon Create(Icon applicationIcon, WidgetVisualState state)
    {
        ArgumentNullException.ThrowIfNull(applicationIcon);

        using var bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        graphics.DrawIcon(applicationIcon, new Rectangle(0, 0, 32, 32));

        var fill = state switch
        {
            WidgetVisualState.Healthy => Color.FromArgb(99, 215, 166),
            WidgetVisualState.Warning => Color.FromArgb(255, 200, 87),
            WidgetVisualState.Critical or WidgetVisualState.Depleted or WidgetVisualState.Error => Color.FromArgb(255, 107, 120),
            _ => Color.FromArgb(130, 148, 138),
        };
        using var ring = new SolidBrush(Color.FromArgb(245, 9, 11, 13));
        using var dot = new SolidBrush(fill);
        graphics.FillEllipse(ring, 20, 20, 12, 12);
        graphics.FillEllipse(dot, 23, 23, 8, 8);

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool DestroyIcon(nint handle);
    }
}
