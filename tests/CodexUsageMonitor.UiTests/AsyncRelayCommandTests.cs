using CodexUsageMonitor.App.Infrastructure;

namespace CodexUsageMonitor.UiTests;

[TestClass]
public sealed class AsyncRelayCommandTests
{
    [TestMethod]
    public async Task ExpectedCommandFailureIsReportedWithoutEscapingAsync()
    {
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AsyncRelayCommand(
            _ => throw new InvalidOperationException("safe-test-failure"),
            onError: exception => reported.TrySetResult(exception));

        command.Execute(null);

        var exception = await reported.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsInstanceOfType<InvalidOperationException>(exception);
        await WaitUntilAsync(() => !command.IsRunning);
        Assert.IsTrue(command.CanExecute(null));
    }

    [TestMethod]
    public async Task CommandCancellationDoesNotInvokeFailureHandlerAsync()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = false;
        var command = new AsyncRelayCommand(
            async cancellationToken =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
            onError: _ => failed = true);

        command.Execute(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        command.Cancel();
        await WaitUntilAsync(() => !command.IsRunning);

        Assert.IsFalse(failed);
        Assert.IsTrue(command.CanExecute(null));
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.Fail("The command did not reach the expected state before the timeout.");
            }

            await Task.Delay(10);
        }
    }
}
