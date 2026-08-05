using CodexUsageMonitor.Codex.Contracts;
using CodexUsageMonitor.Codex.Monitoring;
using CodexUsageMonitor.Codex.Protocol;
using CodexUsageMonitor.App.Services;
using CodexUsageMonitor.Core.Abstractions;
using CodexUsageMonitor.Core.Monitoring;
using CodexUsageMonitor.Persistence.ResetCredits;

namespace CodexUsageMonitor.App.ResetCredits;

public sealed record ResetRedemptionIntent(
    Guid IdempotencyKey,
    Guid ProfileId,
    string AccountStorageKey,
    string AccountLabel,
    string ResetCreditId,
    string ResetCreditLabel,
    IReadOnlyList<string> AffectedLimits,
    DateTimeOffset? ExpiresAtUtc,
    DateTimeOffset PreparedAtUtc);

public sealed record ResetRedemptionOutcome(
    bool Succeeded,
    bool AlreadyRedeemed,
    ResetCreditConsumeOutcome Outcome,
    string Code)
{
    public static ResetRedemptionOutcome Rejected(string code) =>
        new(false, false, ResetCreditConsumeOutcome.Unsupported, code);
}

public sealed class ResetCreditRedemptionService
{
    private static readonly TimeSpan MaximumIntentAge = TimeSpan.FromMinutes(10);
    private readonly UsageApplicationState _state;
    private readonly MultiProfileMonitorCoordinator _monitors;
    private readonly ResetRedemptionRepository _repository;
    private readonly IClock _clock;

    public ResetCreditRedemptionService(
        UsageApplicationState state,
        MultiProfileMonitorCoordinator monitors,
        ResetRedemptionRepository repository,
        IClock clock)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _monitors = monitors ?? throw new ArgumentNullException(nameof(monitors));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<ResetRedemptionIntent> PrepareAsync(
        Guid profileId,
        string resetCreditId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resetCreditId);
        if (!_state.TryGetSnapshot(profileId, out var snapshot) ||
            !_state.TryGetMonitorState(profileId, out var monitor) ||
            monitor.Connection is not MonitorConnectionState.Live)
        {
            throw new InvalidOperationException("A fresh live snapshot is required before redeeming a reset credit.");
        }

        var credit = snapshot.AvailableResetCredits.FirstOrDefault(candidate =>
            candidate.IsRedeemable && string.Equals(candidate.Id, resetCreditId, StringComparison.Ordinal));
        if (credit is null || credit.IsExpired(_clock.UtcNow))
        {
            throw new InvalidOperationException("The selected reset credit is not currently available.");
        }

        var intent = new ResetRedemptionIntent(
            Guid.NewGuid(),
            profileId,
            snapshot.Account.StorageKey,
            snapshot.Account.SafeLabel,
            credit.Id,
            credit.Label,
            credit.AffectedLimitIdentities,
            credit.ExpiresAtUtc,
            _clock.UtcNow);
        var inserted = await _repository.TryBeginAsync(
            new ResetRedemption(
                intent.IdempotencyKey,
                intent.ProfileId,
                intent.AccountStorageKey,
                intent.ResetCreditId,
                intent.PreparedAtUtc,
                null,
                null),
            cancellationToken).ConfigureAwait(false);
        if (!inserted)
        {
            throw new InvalidOperationException("Could not reserve a unique reset-credit attempt.");
        }

        return intent;
    }

    public async Task<ResetRedemptionOutcome> RedeemAsync(
        ResetRedemptionIntent intent,
        bool explicitlyConfirmed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (!explicitlyConfirmed)
        {
            return ResetRedemptionOutcome.Rejected("reset_credit.confirmation_required");
        }

        var now = _clock.UtcNow;
        if (now - intent.PreparedAtUtc > MaximumIntentAge || intent.ExpiresAtUtc is { } expiry && expiry <= now)
        {
            await CompleteAsync(intent, "reset_credit.intent_expired", cancellationToken).ConfigureAwait(false);
            return ResetRedemptionOutcome.Rejected("reset_credit.intent_expired");
        }

        if (!_state.TryGetSnapshot(intent.ProfileId, out var snapshot) ||
            !_state.TryGetMonitorState(intent.ProfileId, out var monitor) ||
            monitor.Connection is not MonitorConnectionState.Live ||
            !string.Equals(snapshot.Account.StorageKey, intent.AccountStorageKey, StringComparison.Ordinal))
        {
            await CompleteAsync(intent, "reset_credit.fresh_identity_required", cancellationToken).ConfigureAwait(false);
            return ResetRedemptionOutcome.Rejected("reset_credit.fresh_identity_required");
        }

        ResetCreditConsumeResult? result;
        try
        {
            result = await _monitors.ConsumeResetCreditAsync(
                intent.ProfileId,
                intent.ResetCreditId,
                intent.IdempotencyKey,
                intent.AccountStorageKey,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or AppServerRpcException)
        {
            // The persisted intent deliberately remains incomplete so a retry can reuse the same key.
            return ResetRedemptionOutcome.Rejected("reset_credit.outcome_uncertain");
        }

        if (result is null)
        {
            return ResetRedemptionOutcome.Rejected("reset_credit.monitor_unavailable");
        }

        await CompleteAsync(intent, result.Code, cancellationToken).ConfigureAwait(false);
        return new ResetRedemptionOutcome(result.Succeeded, result.AlreadyRedeemed, result.Outcome, result.Code);
    }

    private Task CompleteAsync(
        ResetRedemptionIntent intent,
        string outcomeCode,
        CancellationToken cancellationToken) =>
        _repository.CompleteAsync(intent.IdempotencyKey, _clock.UtcNow, outcomeCode, cancellationToken);
}
