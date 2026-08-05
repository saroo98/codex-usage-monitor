using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;
using CodexUsageMonitor.Persistence.History;

namespace CodexUsageMonitor.App.Controls;

public sealed class HistoryChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points),
        typeof(IReadOnlyList<HistoryPoint>),
        typeof(HistoryChart),
        new FrameworkPropertyMetadata(
            Array.Empty<HistoryPoint>(),
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnPointsChanged));

    public IReadOnlyList<HistoryPoint> Points
    {
        get => (IReadOnlyList<HistoryPoint>)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public string AccessibleSummary => Summarize(Points);

    protected override AutomationPeer OnCreateAutomationPeer() => new HistoryChartAutomationPeer(this);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var surface = Brush("SurfaceSubtleBrush", Brushes.Transparent);
        var border = Brush("BorderBrush", Brushes.Gray);
        var muted = Brush("TextMutedBrush", Brushes.Gray);
        var accent = Brush("AccentBrush", Brushes.DodgerBlue);
        drawingContext.DrawRoundedRectangle(surface, new Pen(border, 1), new Rect(0.5, 0.5, Math.Max(0, ActualWidth - 1), Math.Max(0, ActualHeight - 1)), 10, 10);
        var plot = new Rect(42, 16, Math.Max(1, ActualWidth - 58), Math.Max(1, ActualHeight - 42));
        if (plot.Width <= 1 || plot.Height <= 1) return;
        var gridPen = new Pen(border, 1);
        for (var index = 0; index <= 4; index++)
        {
            var y = plot.Top + (plot.Height * index / 4d);
            drawingContext.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = new FormattedText(
                $"{100 - (index * 25)}%",
                System.Globalization.CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                10,
                muted,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            drawingContext.DrawText(label, new Point(7, y - (label.Height / 2)));
        }

        var points = Points?.OrderBy(static point => point.ObservedAtUtc).ToArray() ?? [];
        if (points.Length < 2)
        {
            var empty = new FormattedText(
                "History will appear after confirmed samples are recorded.",
                System.Globalization.CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                12,
                muted,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            drawingContext.DrawText(empty, new Point(plot.Left + 10, plot.Top + (plot.Height - empty.Height) / 2));
            return;
        }

        var start = points[0].ObservedAtUtc;
        var span = Math.Max(1, (points[^1].ObservedAtUtc - start).TotalSeconds);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (var index = 0; index < points.Length; index++)
            {
                var x = plot.Left + (points[index].ObservedAtUtc - start).TotalSeconds / span * plot.Width;
                var y = plot.Bottom - ((double)Math.Clamp(points[index].RemainingPercent, 0m, 100m) / 100d * plot.Height);
                var point = new Point(x, y);
                if (index == 0) context.BeginFigure(point, isFilled: false, isClosed: false);
                else context.LineTo(point, isStroked: true, isSmoothJoin: true);
            }
        }

        geometry.Freeze();
        var linePen = new Pen(accent, 2) { LineJoin = PenLineJoin.Round };
        linePen.Freeze();
        drawingContext.DrawGeometry(null, linePen, geometry);
    }

    private System.Windows.Media.Brush Brush(string key, System.Windows.Media.Brush fallback) =>
        TryFindResource(key) as System.Windows.Media.Brush ?? fallback;

    private static void OnPointsChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is not HistoryChart chart) return;
        var oldSummary = Summarize(eventArgs.OldValue as IReadOnlyList<HistoryPoint>);
        var newSummary = Summarize(eventArgs.NewValue as IReadOnlyList<HistoryPoint>);
        if (UIElementAutomationPeer.FromElement(chart) is HistoryChartAutomationPeer peer)
        {
            peer.NotifySummaryChanged(oldSummary, newSummary);
        }
    }

    public static string Summarize(IReadOnlyList<HistoryPoint>? source)
    {
        var points = source?.OrderBy(static point => point.ObservedAtUtc).ToArray() ?? [];
        if (points.Length == 0) return "No confirmed usage history is available.";
        var first = points[0];
        var last = points[^1];
        var direction = last.RemainingPercent > first.RemainingPercent
            ? "increased"
            : last.RemainingPercent < first.RemainingPercent
                ? "decreased"
                : "was unchanged";
        return FormattableString.Invariant(
            $"{points.Length} confirmed samples from {first.ObservedAtUtc:u} to {last.ObservedAtUtc:u}. Remaining usage {direction} from {first.RemainingPercent:0.#}% to {last.RemainingPercent:0.#}%.");
    }
}

internal sealed class HistoryChartAutomationPeer(HistoryChart owner) : FrameworkElementAutomationPeer(owner), IValueProvider
{
    private HistoryChart Chart => (HistoryChart)Owner;

    bool IValueProvider.IsReadOnly => true;
    string IValueProvider.Value => Chart.AccessibleSummary;

    public override object? GetPattern(PatternInterface patternInterface) =>
        patternInterface is PatternInterface.Value ? this : base.GetPattern(patternInterface);

    protected override string GetClassNameCore() => nameof(HistoryChart);
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
    protected override string GetLocalizedControlTypeCore() => "history chart";
    protected override string GetHelpTextCore() => Chart.AccessibleSummary;
    protected override bool IsContentElementCore() => true;

    void IValueProvider.SetValue(string value) => throw new InvalidOperationException("The history chart is read-only.");

    internal void NotifySummaryChanged(string oldSummary, string newSummary)
    {
        RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, oldSummary, newSummary);
        RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
