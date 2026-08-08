using CodexUsageMonitor.Windows.Windowing;

namespace CodexUsageMonitor.UiTests;

[TestClass]
public sealed class WidgetMoveLifecycleTests
{
    [TestMethod]
    public void ExternalClampRequestsAreDeferredUntilTheMoveEnds()
    {
        var lifecycle = new WidgetMoveLifecycle();
        var clampCount = 0;

        lifecycle.BeginUserMove();

        Assert.IsFalse(lifecycle.RequestExternalClamp(() => clampCount++));
        Assert.IsFalse(lifecycle.RequestExternalClamp(() => clampCount++));
        Assert.AreEqual(0, clampCount);
        Assert.IsTrue(lifecycle.HasDeferredClamp);

        Assert.IsTrue(lifecycle.CompleteUserMove(() => clampCount++));
        Assert.AreEqual(1, clampCount);
        Assert.IsFalse(lifecycle.IsUserMoveActive);
        Assert.IsFalse(lifecycle.HasDeferredClamp);
    }

    [TestMethod]
    public void AValidPositionIsNotClampedWhileTheUserIsDragging()
    {
        var lifecycle = new WidgetMoveLifecycle();
        var clampCount = 0;

        lifecycle.BeginUserMove();
        lifecycle.RequestExternalClamp(() => clampCount++);

        Assert.AreEqual(0, clampCount);
        Assert.IsTrue(lifecycle.CompleteUserMove(() => clampCount++));
        Assert.AreEqual(1, clampCount);
    }

    [TestMethod]
    public void DuplicateMoveEndDoesNotClampAgain()
    {
        var lifecycle = new WidgetMoveLifecycle();
        var clampCount = 0;

        lifecycle.BeginUserMove();

        Assert.IsTrue(lifecycle.CompleteUserMove(() => clampCount++));
        Assert.IsFalse(lifecycle.CompleteUserMove(() => clampCount++));
        Assert.AreEqual(1, clampCount);
    }

    [TestMethod]
    public void ClampRequestsRunImmediatelyWhenNoMoveIsActive()
    {
        var lifecycle = new WidgetMoveLifecycle();
        var clampCount = 0;

        Assert.IsTrue(lifecycle.RequestExternalClamp(() => clampCount++));
        Assert.AreEqual(1, clampCount);
    }
}
