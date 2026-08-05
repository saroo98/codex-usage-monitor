namespace CodexUsageMonitor.Notifications.Delivery;

/// <summary>
/// Coalesces in-process deferred-notification wakeups. SQLite remains the source of truth,
/// so missed pulses are harmless and startup always re-reads the durable queue.
/// </summary>
public sealed class DeferredNotificationSignal : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);
    private int _disposed;

    public void Pulse()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_semaphore.CurrentCount != 0)
        {
            return;
        }

        try
        {
            _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (timeout <= TimeSpan.Zero)
        {
            return;
        }

        await _semaphore.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _semaphore.Dispose();
        }
    }
}
