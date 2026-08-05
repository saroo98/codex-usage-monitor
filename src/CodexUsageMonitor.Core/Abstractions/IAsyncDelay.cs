namespace CodexUsageMonitor.Core.Abstractions;

public interface IAsyncDelay
{
    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemAsyncDelay : IAsyncDelay
{
    public static SystemAsyncDelay Instance { get; } = new();

    private SystemAsyncDelay()
    {
    }

    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        new(Task.Delay(delay, cancellationToken));
}
