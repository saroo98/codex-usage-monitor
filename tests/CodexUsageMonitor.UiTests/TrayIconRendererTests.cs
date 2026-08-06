using System.Drawing;
using System.Runtime.InteropServices;
using CodexUsageMonitor.App.Services;
using CodexUsageMonitor.App.ViewModels;

namespace CodexUsageMonitor.UiTests;

[TestClass]
public sealed class TrayIconRendererTests
{
    [TestMethod]
    public void StateBadgePreservesTheApplicationMark()
    {
        using var sourceBitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(sourceBitmap))
        {
            graphics.Clear(Color.FromArgb(255, 9, 11, 13));
            using var mark = new SolidBrush(Color.FromArgb(255, 245, 245, 241));
            graphics.FillRectangle(mark, 6, 6, 20, 20);
        }

        var handle = sourceBitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            using var sourceIcon = (Icon)temporary.Clone();
            using var rendered = TrayIconRenderer.Create(sourceIcon, WidgetVisualState.Healthy);
            using var bitmap = rendered.ToBitmap();

            Assert.AreEqual(Color.FromArgb(255, 245, 245, 241).ToArgb(), bitmap.GetPixel(12, 12).ToArgb());
            Assert.AreEqual(Color.FromArgb(255, 99, 215, 166).ToArgb(), bitmap.GetPixel(27, 27).ToArgb());
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint handle);
}
