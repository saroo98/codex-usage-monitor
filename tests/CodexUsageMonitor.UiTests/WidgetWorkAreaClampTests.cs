using CodexUsageMonitor.Windows.Windowing;

namespace CodexUsageMonitor.UiTests;

[TestClass]
public sealed class WidgetWorkAreaClampTests
{
    [TestMethod]
    [DataRow(0, 0, 1920, 1040, 100, 1010, 208, 60, 100, 980)]
    [DataRow(0, 40, 1920, 1040, 100, 0, 208, 60, 100, 40)]
    [DataRow(60, 0, 1860, 1080, 0, 100, 208, 60, 60, 100)]
    [DataRow(0, 0, 1860, 1080, 1800, 100, 208, 60, 1652, 100)]
    public void ClampHandlesEveryTaskbarEdge(
        int areaLeft, int areaTop, int areaWidth, int areaHeight,
        int widgetLeft, int widgetTop, int widgetWidth, int widgetHeight,
        int expectedLeft, int expectedTop)
    {
        var result = WidgetWorkAreaClamp.ClampWidgetToMonitorWorkArea(
            new PixelRect(widgetLeft, widgetTop, widgetWidth, widgetHeight),
            new PixelRect(areaLeft, areaTop, areaWidth, areaHeight));

        Assert.AreEqual(expectedLeft, result.Left);
        Assert.AreEqual(expectedTop, result.Top);
    }

    [TestMethod]
    public void ClampPreservesValidNegativeMultiMonitorCoordinates()
    {
        var area = new PixelRect(-2560, -200, 2560, 1400);
        var widget = new PixelRect(-1200, 300, 312, 90);

        Assert.AreEqual(widget, WidgetWorkAreaClamp.ClampWidgetToMonitorWorkArea(widget, area));
    }

    [TestMethod]
    public void ClampMovesAnInvalidSavedPositionByTheMinimumDistance()
    {
        var result = WidgetWorkAreaClamp.ClampWidgetToMonitorWorkArea(
            new PixelRect(4000, 2100, 208, 60),
            new PixelRect(1920, 0, 1920, 1040));

        Assert.AreEqual(new PixelRect(3632, 980, 208, 60), result);
    }

    [TestMethod]
    public void HighDpiWidgetUsesPhysicalSizeBeforeClamping()
    {
        var size = WidgetWorkAreaClamp.DipSizeToPixels(208, 60, 1.5, 1.5);
        var result = WidgetWorkAreaClamp.ClampWidgetToMonitorWorkArea(
            new PixelRect(1700, 1000, size.Width, size.Height),
            new PixelRect(0, 0, 1920, 1040));

        Assert.AreEqual(new PixelRect(1608, 950, 312, 90), result);
    }

    [TestMethod]
    public void WidgetLargerThanWorkAreaIsReducedAndContained()
    {
        var area = new PixelRect(100, 200, 160, 40);

        var result = WidgetWorkAreaClamp.ClampWidgetToMonitorWorkArea(
            new PixelRect(80, 180, 208, 60),
            area);

        Assert.AreEqual(area, result);
    }

    [TestMethod]
    public void ReleasingOverBottomTaskbarClampsOnceToTheNearestWorkAreaEdge()
    {
        var area = new PixelRect(0, 0, 1920, 1040);
        var attempted = new PixelRect(720, 1018, 208, 60);

        Assert.AreEqual(new PixelRect(720, 980, 208, 60), WidgetWorkAreaClamp.ClampWidgetToMonitorWorkArea(attempted, area));
    }

    [TestMethod]
    public void OptInTaskbarPlacementUsesFullMonitorBoundsButRemainsOnScreen()
    {
        var bounds = new PixelRect(0, 0, 1920, 1080);
        var workArea = new PixelRect(0, 0, 1920, 1040);
        var attempted = new PixelRect(720, 1038, 208, 60);
        var placementArea = MonitorPlacementService.SelectPlacementArea(bounds, workArea, allowTaskbarOverlap: true);

        Assert.AreEqual(new PixelRect(720, 1020, 208, 60), WidgetWorkAreaClamp.ClampWidgetToMonitorWorkArea(attempted, placementArea));
    }

    [TestMethod]
    public void ReleasingPartiallyOutsideEveryMonitorEdgeUsesMinimumMovement()
    {
        var area = new PixelRect(-1920, -40, 1920, 1040);

        Assert.AreEqual(
            new PixelRect(-1920, -40, 208, 60),
            WidgetWorkAreaClamp.ClampWidgetToMonitorWorkArea(new PixelRect(-1942, -66, 208, 60), area));
        Assert.AreEqual(
            new PixelRect(-208, 940, 208, 60),
            WidgetWorkAreaClamp.ClampWidgetToMonitorWorkArea(new PixelRect(-208, 1002, 208, 60), area));
    }

    [TestMethod]
    public void FinalPositionOnAnotherNegativeCoordinateMonitorUsesThatMonitorWorkArea()
    {
        var secondaryWorkArea = new PixelRect(-2560, -200, 2560, 1400);
        var attempted = new PixelRect(-2535, 1155, 312, 90);

        Assert.AreEqual(new PixelRect(-2535, 1110, 312, 90), WidgetWorkAreaClamp.ClampWidgetToMonitorWorkArea(attempted, secondaryWorkArea));
    }

    [TestMethod]
    public void HighDpiAndNearEdgePositionRemainInsideTheSelectedWorkArea()
    {
        var area = new PixelRect(-1600, 0, 1600, 900);
        var size = WidgetWorkAreaClamp.DipSizeToPixels(208, 60, 1.5, 1.5);

        Assert.AreEqual(
            new PixelRect(-1599, 810, size.Width, size.Height),
            WidgetWorkAreaClamp.ClampWidgetToMonitorWorkArea(new PixelRect(-1599, 880, size.Width, size.Height), area));
    }
}
