namespace CodexUsageMonitor.Windows.Windowing;

public readonly record struct DipRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;

    public bool IsFinite =>
        double.IsFinite(Left) && double.IsFinite(Top) &&
        double.IsFinite(Width) && double.IsFinite(Height) && Width > 0 && Height > 0;

    public DipRect WithPosition(double left, double top) => new(left, top, Width, Height);
}

public sealed record MonitorWorkArea(
    string DeviceName,
    DipRect BoundsDip,
    DipRect WorkAreaDip,
    double DpiScaleX,
    double DpiScaleY,
    bool IsPrimary);
