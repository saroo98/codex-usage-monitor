namespace CodexUsageMonitor.Windows.Windowing;

public static class EdgeSnapper
{
    public const double DefaultThresholdDip = 12;
    public const double DefaultGapDip = 8;

    public static DipRect ClampAndSnap(
        DipRect proposed,
        DipRect workArea,
        bool snap,
        double thresholdDip = DefaultThresholdDip,
        double gapDip = DefaultGapDip)
    {
        if (!proposed.IsFinite || !workArea.IsFinite)
        {
            throw new ArgumentOutOfRangeException(nameof(proposed));
        }

        thresholdDip = Math.Max(0, thresholdDip);
        gapDip = Math.Max(0, gapDip);
        var minimumLeft = workArea.Left + gapDip;
        var maximumLeft = Math.Max(minimumLeft, workArea.Right - proposed.Width - gapDip);
        var minimumTop = workArea.Top + gapDip;
        var maximumTop = Math.Max(minimumTop, workArea.Bottom - proposed.Height - gapDip);
        var left = Math.Clamp(proposed.Left, minimumLeft, maximumLeft);
        var top = Math.Clamp(proposed.Top, minimumTop, maximumTop);

        if (snap)
        {
            if (Math.Abs(left - minimumLeft) <= thresholdDip)
            {
                left = minimumLeft;
            }
            else if (Math.Abs(left - maximumLeft) <= thresholdDip)
            {
                left = maximumLeft;
            }

            if (Math.Abs(top - minimumTop) <= thresholdDip)
            {
                top = minimumTop;
            }
            else if (Math.Abs(top - maximumTop) <= thresholdDip)
            {
                top = maximumTop;
            }
        }

        return proposed.WithPosition(left, top);
    }

    public static bool HasExceededDragThreshold(double originX, double originY, double currentX, double currentY, double thresholdDip = 4) =>
        Math.Abs(currentX - originX) >= thresholdDip || Math.Abs(currentY - originY) >= thresholdDip;
}
