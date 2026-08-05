using CodexUsageMonitor.Core.Abstractions;
using CodexUsageMonitor.Email.Models;
using CodexUsageMonitor.Persistence.Outbox;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.Email.Outbox;

public sealed class EmailOutboxProcessor
{
    private const int MaximumAttempts = 8;
    private const int MaximumBatchSize = 32;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMinutes(15);
    private readonly EmailOutboxRepository _repository;
    private readonly EmailOutboxPayloadCodec _codec;
    private readonly Func<EmailOutboxItem, IEmailTransport?> _transportResolver;
    private readonly IClock _clock;
    private readonly EmailOutboxSignal _signal;
    private readonly EmailRetryBackoffPolicy _retryPolicy;
    private readonly ILogger<EmailOutboxProcessor> _logger;

    public EmailOutboxProcessor(
        EmailOutboxRepository repository,
        EmailOutboxPayloadCodec codec,
        Func<EmailOutboxItem, IEmailTransport?> transportResolver,
        IClock clock,
        EmailOutboxSignal signal,
        EmailRetryBackoffPolicy retryPolicy,
        ILogger<EmailOutboxProcessor> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _transportResolver = transportResolver ?? throw new ArgumentNullException(nameof(transportResolver));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _retryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var workerFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessDueBatchAsync(MaximumBatchSize, cancellationToken).ConfigureAwait(false);
                var now = _clock.UtcNow;
                await _repository.CleanupAsync(now, cancellationToken).ConfigureAwait(false);
                workerFailures = 0;

                if (processed == MaximumBatchSize)
                {
                    await Task.Yield();
                    continue;
                }

                var next = await _repository.GetNextPendingAtAsync(now, cancellationToken).ConfigureAwait(false);
                var wait = next is null
                    ? MaintenanceInterval
                    : ClampWait(next.Value - now);
                await _signal.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                workerFailures++;
                var delay = ClampWait(_retryPolicy.DelayForAttempt(Math.Min(workerFailures, MaximumAttempts)));
                _logger.LogError(
                    exception,
                    "Email outbox processing failed. The durable queue will be retried after {RetryDelay}.",
                    delay);
                await _signal.WaitAsync(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<int> ProcessDueBatchAsync(int maximumItems, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumItems);

        var processed = 0;
        while (processed < maximumItems && await ProcessOneAsync(cancellationToken).ConfigureAwait(false))
        {
            processed++;
            cancellationToken.ThrowIfCancellationRequested();
        }

        return processed;
    }

    public async Task<bool> ProcessOneAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var item = await _repository.TryLeaseNextAsync(now, LeaseDuration, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return false;
        }

        if (item.ExpiresAtUtc <= now || item.AttemptCount >= MaximumAttempts)
        {
            await _repository.MarkTerminalAsync(item.Id, now, "email.outbox_expired", cancellationToken).ConfigureAwait(false);
            return true;
        }

        EmailMessage message;
        try
        {
            message = _codec.Decode(item.PayloadJson);
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Security.Cryptography.CryptographicException)
        {
            _logger.LogWarning(exception, "Email outbox item {ItemId} could not be decoded.", item.Id);
            await _repository.MarkTerminalAsync(item.Id, now, "email.payload_unreadable", cancellationToken).ConfigureAwait(false);
            return true;
        }

        IEmailTransport? transport;
        try
        {
            transport = _transportResolver(item);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            _logger.LogWarning(exception, "Email transport resolution failed for outbox item {ItemId}.", item.Id);
            await _repository.MarkTerminalAsync(item.Id, now, "email.transport_resolution_failed", cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (transport is null)
        {
            await _repository.MarkTerminalAsync(item.Id, now, "email.transport_unconfigured", cancellationToken).ConfigureAwait(false);
            return true;
        }

        EmailDeliveryResult result;
        try
        {
            result = await transport.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Email transport threw an unexpected exception for outbox item {ItemId}.", item.Id);
            result = EmailDeliveryResult.Transient("email.unexpected_transport_failure");
        }

        if (result.Delivered)
        {
            await _repository.CompleteAsync(item.Id, cancellationToken).ConfigureAwait(false);
            return true;
        }

        var nextAttempt = item.AttemptCount + 1;
        if (!result.IsTransient || nextAttempt >= MaximumAttempts)
        {
            await _repository.MarkTerminalAsync(
                item.Id,
                now,
                result.SafeErrorCode ?? "email.delivery_rejected",
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        await _repository.RetryAsync(
            item.Id,
            nextAttempt,
            now + _retryPolicy.DelayForAttempt(nextAttempt),
            result.SafeErrorCode ?? "email.delivery_deferred",
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static TimeSpan ClampWait(TimeSpan wait)
    {
        if (wait <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return wait > MaintenanceInterval ? MaintenanceInterval : wait;
    }
}
