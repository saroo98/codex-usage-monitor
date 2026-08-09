namespace CodexUsageMonitor.Windows.Windowing;

public readonly record struct PixelRect(int Left, int Top, int Width, int Height)
{
    public int Right => checked(Left + Width);
    public int Bottom => checked(Top + Height);
}

public static class WidgetWorkAreaClamp
{
    public static PixelRect ClampWidgetToMonitorWorkArea(PixelRect widget, PixelRect workArea)
    {
        if (widget.Width <= 0 || widget.Height <= 0 || workArea.Width <= 0 || workArea.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(widget));
        }

        var width = Math.Min(widget.Width, workArea.Width);
        var height = Math.Min(widget.Height, workArea.Height);
        var maximumLeft = checked(workArea.Right - width);
        var maximumTop = checked(workArea.Bottom - height);
        return new PixelRect(
            Math.Clamp(widget.Left, workArea.Left, maximumLeft),
            Math.Clamp(widget.Top, workArea.Top, maximumTop),
            width,
            height);
    }

    public static (int Width, int Height) DipSizeToPixels(double widthDip, double heightDip, double dpiScaleX, double dpiScaleY)
    {
        if (!double.IsFinite(widthDip) || !double.IsFinite(heightDip) ||
            !double.IsFinite(dpiScaleX) || !double.IsFinite(dpiScaleY) ||
            widthDip <= 0 || heightDip <= 0 || dpiScaleX <= 0 || dpiScaleY <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(widthDip));
        }

        return (
            checked((int)Math.Round(widthDip * dpiScaleX, MidpointRounding.AwayFromZero)),
            checked((int)Math.Round(heightDip * dpiScaleY, MidpointRounding.AwayFromZero)));
    }
}
