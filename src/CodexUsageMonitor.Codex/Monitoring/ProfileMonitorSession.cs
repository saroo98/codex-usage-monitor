using System.Text.Json;
using System.Threading.Channels;
using CodexUsageMonitor.Application.Monitoring;
using CodexUsageMonitor.Codex.Contracts;
using CodexUsageMonitor.Codex.Mapping;
using CodexUsageMonitor.Core.Abstractions;
using CodexUsageMonitor.Core.Monitoring;
using CodexUsageMonitor.Core.Profiles;
using CodexUsageMonitor.Core.Usage;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.Codex.Monitoring;

public sealed class ProfileMonitorSession : IAsyncDisposable
{
    private readonly ProfileDefinition _profile;
    private readonly IAppServerClient _client;
    private readonly CodexSnapshotMapper _mapper;
    private readonly SparseRateLimitsMerger _merger;
    private readonly UsageStateReducer _reducer;
    private readonly IProfileMonitorCallbacks _callbacks;
    private readonly IClock _clock;
    private readonly ILogger<ProfileMonitorSession> _logger;
    private readonly Channel<MonitorCommand> _commands = Channel.CreateBounded<MonitorCommand>(new BoundedChannelOptions(8)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly Channel<RateLimitsReadResult> _updates = Channel.CreateBounded<RateLimitsReadResult>(new BoundedChannelOptions(4)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
    });
    private AccountReadResult? _account;
    private MonitorState _state = MonitorState.Initial;
    private DateTimeOffset _lastManualRefreshUtc = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public ProfileMonitorSession(
        ProfileDefinition profile,
        IAppServerClient client,
        CodexSnapshotMapper mapper,
        SparseRateLimitsMerger merger,
        UsageStateReducer reducer,
        IProfileMonitorCallbacks callbacks,
        IClock clock,
        ILogger<ProfileMonitorSession> logger)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _merger = merger ?? throw new ArgumentNullException(nameof(merger));
        _reducer = reducer ?? throw new ArgumentNullException(nameof(reducer));
        _callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _client.RateLimitsUpdated += OnRateLimitsUpdated;
    }

    public MonitorState State => _state;

    public bool RequestRefresh(bool manual)
    {
        if (manual && _clock.UtcNow - _lastManualRefreshUtc < TimeSpan.FromSeconds(3))
        {
            return false;
        }

        if (manual)
        {
            _lastManualRefreshUtc = _clock.UtcNow;
        }

        return _commands.Writer.TryWrite(new MonitorCommand(manual));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await SetStateAsync(_state with { Connection = MonitorConnectionState.Starting }, cancellationToken).ConfigureAwait(false);
        await _client.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await RefreshFullAsync(cancellationToken).ConfigureAwait(false);
        using var periodic = new PeriodicTimer(TimeSpan.FromMinutes(5));
        var periodicReady = periodic.WaitForNextTickAsync(cancellationToken).AsTask();
        while (!cancellationToken.IsCancellationRequested)
        {
            var commandReady = _commands.Reader.WaitToReadAsync(cancellationToken).AsTask();
            var updateReady = _updates.Reader.WaitToReadAsync(cancellationToken).AsTask();
            var completed = await Task.WhenAny(commandReady, updateReady, periodicReady).ConfigureAwait(false);
            if (completed == commandReady && await commandReady.ConfigureAwait(false))
            {
                while (_commands.Reader.TryRead(out _))
                {
                }

                await RefreshFullAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (completed == updateReady && await updateReady.ConfigureAwait(false))
            {
                while (_updates.Reader.TryRead(out var update))
                {
                    await ApplySparseAsync(update, cancellationToken).ConfigureAwait(false);
                }
            }
            else if (completed == periodicReady && await periodicReady.ConfigureAwait(false))
            {
                await RefreshFullAsync(cancellationToken).ConfigureAwait(false);
                periodicReady = periodic.WaitForNextTickAsync(cancellationToken).AsTask();
            }
        }
    }

    private async Task RefreshFullAsync(CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshFullCoreAsync(refreshToken: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task RefreshFullCoreAsync(bool refreshToken, CancellationToken cancellationToken)
    {
        try
        {
            _account = await _client.ReadAccountAsync(refreshToken, cancellationToken).ConfigureAwait(false);
            var rateLimits = await _client.ReadRateLimitsAsync(cancellationToken).ConfigureAwait(false);
            _merger.Reset(rateLimits.Raw);
            await MapAndPublishAsync(rateLimits.Raw, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or Protocol.AppServerRpcException)
        {
            _logger.LogWarning(exception, "Profile {ProfileId} refresh failed.", _profile.Id);
            var read = new MonitorReadResult(
                false,
                null,
                exception is Protocol.AppServerRpcException rpc && rpc.RpcCode is -32001
                    ? "codex.authentication_required"
                    : "codex.refresh_failed",
                IsAuthenticationFailure: exception is Protocol.AppServerRpcException auth && auth.RpcCode is -32001);
            await SetStateAsync(_reducer.Apply(_state, read, _clock.UtcNow), cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ResetCreditConsumeResult> ConsumeResetCreditAsync(
        string? creditId,
        Guid idempotencyKey,
        string expectedAccountStorageKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedAccountStorageKey);
        if (idempotencyKey == Guid.Empty)
        {
            throw new ArgumentException("Idempotency key cannot be empty.", nameof(idempotencyKey));
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _account = await _client.ReadAccountAsync(refreshToken: true, cancellationToken).ConfigureAwait(false);
            var before = await _client.ReadRateLimitsAsync(cancellationToken).ConfigureAwait(false);
            var mapped = _mapper.Map(_account.Raw, before.Raw);
            if (!string.Equals(mapped.Account.StorageKey, expectedAccountStorageKey, StringComparison.Ordinal))
            {
                return ResetCreditConsumeResult.Rejected("reset_credit.account_changed");
            }

            var available = string.IsNullOrWhiteSpace(creditId)
                ? mapped.ResetCredits.FirstOrDefault(static credit => credit.IsRedeemable)
                : mapped.ResetCredits.FirstOrDefault(credit =>
                    credit.IsRedeemable && string.Equals(credit.Id, creditId, StringComparison.Ordinal));
            if (available is null || available.IsExpired(_clock.UtcNow))
            {
                return ResetCreditConsumeResult.Rejected("reset_credit.unavailable");
            }

            var result = await _client.ConsumeResetCreditAsync(creditId, idempotencyKey, cancellationToken).ConfigureAwait(false);
            if (result.ShouldRefreshLimits)
            {
                await RefreshFullCoreAsync(refreshToken: false, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task ApplySparseAsync(RateLimitsReadResult update, CancellationToken cancellationToken)
    {
        if (_account is null)
        {
            await RefreshFullAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var merged = _merger.Merge(update.Raw);
            await MapAndPublishAsync(merged, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            await RefreshFullAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task MapAndPublishAsync(JsonElement rateLimits, CancellationToken cancellationToken)
    {
        if (_account is null)
        {
            throw new InvalidOperationException("Account identity must be read before usage data is mapped.");
        }

        var mapped = _mapper.Map(_account.Raw, rateLimits);
        var snapshot = new UsageSnapshot(
            _profile.Id,
            mapped.Account,
            _clock.UtcNow,
            mapped.Limits,
            mapped.ResetCredits,
            mapped.Workspace,
            mapped.Sequence);
        _state = _reducer.Apply(_state, new MonitorReadResult(true, snapshot, "codex.snapshot"), _clock.UtcNow);
        await _callbacks.SnapshotReceivedAsync(_profile, snapshot, cancellationToken).ConfigureAwait(false);
        await _callbacks.StateChangedAsync(_profile, _state, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask SetStateAsync(MonitorState state, CancellationToken cancellationToken)
    {
        _state = state;
        await _callbacks.StateChangedAsync(_profile, state, cancellationToken).ConfigureAwait(false);
    }

    private void OnRateLimitsUpdated(object? sender, RateLimitsReadResult update) => _updates.Writer.TryWrite(update);

    public async ValueTask DisposeAsync()
    {
        _client.RateLimitsUpdated -= OnRateLimitsUpdated;
        _commands.Writer.TryComplete();
        _updates.Writer.TryComplete();
        await _client.DisposeAsync().ConfigureAwait(false);
        _operationGate.Dispose();
    }

    private sealed record MonitorCommand(bool Manual);
}
