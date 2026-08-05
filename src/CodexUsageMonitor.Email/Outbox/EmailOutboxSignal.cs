using System.Threading.Channels;

namespace CodexUsageMonitor.Email.Outbox;

public enum EmailOutboxWakeReason
{
    Enqueued,
    ConfigurationChanged,
    SystemResumed,
    Manual,
}

/// <summary>
/// Coalesces in-process wakeups for the durable email outbox. SQLite remains the
/// source of truth, so a wakeup may be dropped safely as long as the worker also
/// performs bounded maintenance reads.
/// </summary>
public sealed class EmailOutboxSignal : IDisposable
{
    private readonly Channel<EmailOutboxWakeReason> _channel = Channel.CreateBounded<EmailOutboxWakeReason>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private int _disposed;

    public void Pulse() => Pulse(EmailOutboxWakeReason.Enqueued);

    public void Pulse(EmailOutboxWakeReason reason)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _channel.Writer.TryWrite(reason);
    }

    public async Task<EmailOutboxWakeReason?> WaitAsync(
        TimeSpan maximumDelay,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDelay, Timeout.InfiniteTimeSpan);

        if (_channel.Reader.TryRead(out var immediate))
        {
            return DrainLatest(immediate);
        }

        if (maximumDelay == TimeSpan.Zero)
        {
            return null;
        }

        try
        {
            EmailOutboxWakeReason reason;
            if (maximumDelay == Timeout.InfiniteTimeSpan)
            {
                reason = await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(maximumDelay);
                try
                {
                    reason = await _channel.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return null;
                }
            }

            return DrainLatest(reason);
        }
        catch (ChannelClosedException) when (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(EmailOutboxSignal));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _channel.Writer.TryComplete();
        }
    }

    private EmailOutboxWakeReason DrainLatest(EmailOutboxWakeReason reason)
    {
        while (_channel.Reader.TryRead(out var newer))
        {
            reason = newer;
        }

        return reason;
    }
}
