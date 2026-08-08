using CodexUsageMonitor.Core.Abstractions;
using CodexUsageMonitor.Email.Models;
using CodexUsageMonitor.Persistence.Outbox;

namespace CodexUsageMonitor.Email.Outbox;

public sealed class EmailOutboxQueue
{
    private readonly EmailOutboxRepository _repository;
    private readonly EmailOutboxPayloadCodec _codec;
    private readonly IClock _clock;
    private readonly EmailOutboxSignal _signal;

    public EmailOutboxQueue(
        EmailOutboxRepository repository,
        EmailOutboxPayloadCodec codec,
        IClock clock,
        EmailOutboxSignal signal)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
    }

    public Task<bool> EnqueueAsync(
        SelfNotification message,
        Guid profileId,
        string accountKey,
        DateTimeOffset availableAtUtc,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("Profile ID cannot be empty.", nameof(profileId));
        }

        var now = _clock.UtcNow;
        availableAtUtc = availableAtUtc.ToUniversalTime();
        expiresAtUtc = expiresAtUtc.ToUniversalTime();
        if (availableAtUtc < now)
        {
            availableAtUtc = now;
        }

        if (expiresAtUtc <= availableAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAtUtc), "Expiry must be after the delivery time.");
        }

        var item = new EmailOutboxItem(
            Guid.NewGuid(),
            message.DeduplicationKey,
            profileId,
            accountKey,
            _codec.Encode(message),
            now,
            availableAtUtc,
            expiresAtUtc,
            0,
            null,
            null,
            null,
            null);
        return EnqueueAndSignalAsync(item, cancellationToken);
    }

    private async Task<bool> EnqueueAndSignalAsync(EmailOutboxItem item, CancellationToken cancellationToken)
    {
        var queued = await _repository.TryEnqueueAsync(item, cancellationToken).ConfigureAwait(false);
        if (queued)
        {
            _signal.Pulse(EmailOutboxWakeReason.Enqueued);
        }

        return queued;
    }
}
