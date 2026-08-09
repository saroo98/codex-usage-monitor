using CodexUsageMonitor.Windows.Windowing;

namespace CodexUsageMonitor.UiTests;

[TestClass]
public sealed class WidgetDragSessionTests
{
    [TestMethod]
    public void PointerMovementIsNotClampedDuringAnActiveDrag()
    {
        var session = new WidgetDragSession();
        session.Begin(new WidgetDragPoint(100, 100), new PixelRect(700, 900, 208, 60));

        Assert.IsTrue(session.TryMove(new WidgetDragPoint(100, 1200), out var left, out var top));
        Assert.AreEqual(700, left);
        Assert.AreEqual(2000, top);
        Assert.IsTrue(session.IsActive);
        Assert.IsTrue(session.HasMoved);
    }

    [TestMethod]
    public void CompletionReportsOneMovedDragAndThenStops()
    {
        var session = new WidgetDragSession();
        session.Begin(new WidgetDragPoint(20, 20), new PixelRect(-1920, -40, 208, 60));
        session.TryMove(new WidgetDragPoint(0, 0), out _, out _);

        Assert.IsTrue(session.Complete());
        Assert.IsFalse(session.IsActive);
        Assert.IsFalse(session.Complete());
    }

    [TestMethod]
    public void AClickWithoutMovementIsNotReportedAsADrag()
    {
        var session = new WidgetDragSession();
        session.Begin(new WidgetDragPoint(20, 20), new PixelRect(100, 100, 208, 60));

        Assert.IsFalse(session.Complete());
        Assert.IsFalse(session.IsActive);
    }

    [TestMethod]
    public void LostCaptureCanCancelWithoutLeavingActiveState()
    {
        var session = new WidgetDragSession();
        session.Begin(new WidgetDragPoint(20, 20), new PixelRect(100, 100, 208, 60));
        session.Cancel();

        Assert.IsFalse(session.IsActive);
        Assert.IsFalse(session.HasMoved);
    }
}
