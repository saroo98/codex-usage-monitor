using CodexUsageMonitor.Notifications.Delivery;

namespace CodexUsageMonitor.UiTests;

[TestClass]
public sealed class DeferredNotificationSignalTests
{
    [TestMethod]
    public async Task PulseCompletesPendingWait()
    {
        using var signal = new DeferredNotificationSignal();
        var wait = signal.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        signal.Pulse();
        await wait;
        Assert.IsTrue(wait.IsCompletedSuccessfully);
    }
}
