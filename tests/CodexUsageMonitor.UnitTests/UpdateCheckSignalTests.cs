using CodexUsageMonitor.Application.Updates;

namespace CodexUsageMonitor.UnitTests;

[TestClass]
public sealed class UpdateCheckSignalTests
{
    [TestMethod]
    public async Task PulseWakesWaiterAndCoalescesToLatestReason()
    {
        using var signal = new UpdateCheckSignal();
        signal.Pulse(UpdateWakeReason.SettingsChanged);
        signal.Pulse(UpdateWakeReason.Manual);

        var reason = await signal.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.AreEqual(UpdateWakeReason.Manual, reason);
    }

    [TestMethod]
    public async Task TimeoutReturnsNullWithoutLeavingAReadBehind()
    {
        using var signal = new UpdateCheckSignal();

        var first = await signal.WaitAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None);
        signal.Pulse(UpdateWakeReason.SystemResumed);
        var second = await signal.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.IsNull(first);
        Assert.AreEqual(UpdateWakeReason.SystemResumed, second);
    }
}
