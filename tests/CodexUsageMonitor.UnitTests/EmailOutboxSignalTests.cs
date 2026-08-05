using CodexUsageMonitor.Email.Outbox;

namespace CodexUsageMonitor.UnitTests;

[TestClass]
public sealed class EmailOutboxSignalTests
{
    [TestMethod]
    public async Task PulseWakesWaiterAndCoalescesMultipleProducers()
    {
        using var signal = new EmailOutboxSignal();
        signal.Pulse(EmailOutboxWakeReason.Enqueued);
        signal.Pulse(EmailOutboxWakeReason.ConfigurationChanged);

        var started = DateTimeOffset.UtcNow;
        var reason = await signal.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.AreEqual(EmailOutboxWakeReason.ConfigurationChanged, reason);
        Assert.IsTrue(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    public async Task WaitReturnsNullAtTimeoutWithoutStealingFuturePulse()
    {
        using var signal = new EmailOutboxSignal();

        var reason = await signal.WaitAsync(TimeSpan.FromMilliseconds(25), CancellationToken.None);
        signal.Pulse(EmailOutboxWakeReason.Manual);
        var next = await signal.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.IsNull(reason);
        Assert.AreEqual(EmailOutboxWakeReason.Manual, next);
    }

    [TestMethod]
    public async Task InfiniteWaitCanBeCancelledWithoutPolling()
    {
        using var signal = new EmailOutboxSignal();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            signal.WaitAsync(Timeout.InfiniteTimeSpan, cancellation.Token));
    }

    [TestMethod]
    public void DisposedSignalRejectsNewPulses()
    {
        var signal = new EmailOutboxSignal();
        signal.Dispose();

        Assert.Throws<ObjectDisposedException>(() => signal.Pulse(EmailOutboxWakeReason.Manual));
    }
}
