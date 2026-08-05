using CodexUsageMonitor.Codex.Contracts;

namespace CodexUsageMonitor.Codex;

public interface IAppServerClient : IAsyncDisposable
{
    bool IsInitialized { get; }

    event EventHandler<RateLimitsReadResult>? RateLimitsUpdated;

    Task<AppServerInitialization> InitializeAsync(CancellationToken cancellationToken);

    Task<AccountReadResult> ReadAccountAsync(bool refreshToken, CancellationToken cancellationToken);

    Task<RateLimitsReadResult> ReadRateLimitsAsync(CancellationToken cancellationToken);

    Task<UsageReadResult> ReadUsageAsync(CancellationToken cancellationToken);

    Task<ResetCreditConsumeResult> ConsumeResetCreditAsync(
        string? creditId,
        Guid idempotencyKey,
        CancellationToken cancellationToken);
}
