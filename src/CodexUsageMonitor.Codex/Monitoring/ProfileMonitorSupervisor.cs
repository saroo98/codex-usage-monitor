using CodexUsageMonitor.Application.Monitoring;
using CodexUsageMonitor.Codex.Mapping;
using CodexUsageMonitor.Codex.Transport;
using CodexUsageMonitor.Core.Abstractions;
using CodexUsageMonitor.Core.Diagnostics;
using CodexUsageMonitor.Core.Monitoring;
using CodexUsageMonitor.Core.Profiles;
using CodexUsageMonitor.Core.Scheduling;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.Codex.Monitoring;

public sealed class ProfileMonitorSupervisor
{
    private readonly AppServerClientFactory _clientFactory;
    private readonly CodexExecutableResolver _resolver;
    private readonly IProfileMonitorCallbacks _callbacks;
    private readonly IClock _clock;
    private readonly IAsyncDelay _delay;
    private readonly RetryBackoffPolicy _backoff;
    private readonly IProtocolAnomalySink _anomalySink;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ProfileMonitorSupervisor> _logger;
    private ProfileMonitorSession? _activeSession;

    public ProfileMonitorSupervisor(
        AppServerClientFactory clientFactory,
        CodexExecutableResolver resolver,
        IProfileMonitorCallbacks callbacks,
        IClock clock,
        IAsyncDelay delay,
        RetryBackoffPolicy backoff,
        IProtocolAnomalySink anomalySink,
        ILoggerFactory loggerFactory)
    {
        _clientFactory = clientFactory;
        _resolver = resolver;
        _callbacks = callbacks;
        _clock = clock;
        _delay = delay;
        _backoff = backoff;
        _anomalySink = anomalySink;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ProfileMonitorSupervisor>();
    }

    public bool RequestRefresh(bool manual = true) =>
        Volatile.Read(ref _activeSession)?.RequestRefresh(manual) ?? false;

    public async Task<Contracts.ResetCreditConsumeResult?> ConsumeResetCreditAsync(
        string? creditId,
        Guid idempotencyKey,
        string expectedAccountStorageKey,
        CancellationToken cancellationToken)
    {
        var session = Volatile.Read(ref _activeSession);
        return session is null
            ? null
            : await session.ConsumeResetCreditAsync(
                creditId,
                idempotencyKey,
                expectedAccountStorageKey,
                cancellationToken).ConfigureAwait(false);
    }

    public async Task RunAsync(ProfileDefinition profile, CancellationToken cancellationToken)
    {
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var command = _resolver.Resolve();
            if (command is null)
            {
                failures++;
                await _callbacks.StateChangedAsync(
                    profile,
                    MonitorState.Initial with
                    {
                        Connection = MonitorConnectionState.CodexUnavailable,
                        SafeErrorCode = "codex.not_found",
                        ConsecutiveFailures = failures,
                        LastAttemptAtUtc = _clock.UtcNow,
                    },
                    cancellationToken).ConfigureAwait(false);
                await _delay.DelayAsync(_backoff.DelayFor(failures), cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await using var session = new ProfileMonitorSession(
                    profile,
                    _clientFactory.Create(command, profile.CodexHome),
                    new CodexSnapshotMapper(_anomalySink),
                    new SparseRateLimitsMerger(),
                    new UsageStateReducer(new SnapshotCoherenceValidator(_anomalySink)),
                    _callbacks,
                    _clock,
                    _loggerFactory.CreateLogger<ProfileMonitorSession>());
                Volatile.Write(ref _activeSession, session);
                try
                {
                    await session.RunAsync(cancellationToken).ConfigureAwait(false);
                    failures = 0;
                }
                finally
                {
                    Interlocked.CompareExchange(ref _activeSession, null, session);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or Protocol.AppServerRpcException)
            {
                failures++;
                _logger.LogWarning(exception, "Codex profile {ProfileId} disconnected; retry {Retry}.", profile.Id, failures);
                await _delay.DelayAsync(_backoff.DelayFor(failures), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
