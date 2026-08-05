using System.Threading.Channels;

namespace CodexUsageMonitor.Application.Updates;

public enum UpdateWakeReason
{
    SettingsChanged,
    Manual,
    SystemResumed,
}

public sealed class UpdateCheckSignal : IDisposable
{
    private readonly Channel<UpdateWakeReason> _channel = Channel.CreateBounded<UpdateWakeReason>(
        new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false,
        });
    private int _disposed;

    public void Pulse(UpdateWakeReason reason)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _channel.Writer.TryWrite(reason);
    }

    public async Task<UpdateWakeReason?> WaitAsync(TimeSpan maximumDelay, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDelay, Timeout.InfiniteTimeSpan);

        if (_channel.Reader.TryRead(out var immediate))
        {
            return Drain(immediate);
        }

        if (maximumDelay == TimeSpan.Zero)
        {
            return null;
        }

        try
        {
            UpdateWakeReason reason;
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

            return Drain(reason);
        }
        catch (ChannelClosedException) when (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(UpdateCheckSignal));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _channel.Writer.TryComplete();
        }
    }

    private UpdateWakeReason Drain(UpdateWakeReason reason)
    {
        while (_channel.Reader.TryRead(out var newer))
        {
            reason = newer;
        }

        return reason;
    }
}
