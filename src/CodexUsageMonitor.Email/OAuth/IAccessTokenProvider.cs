namespace CodexUsageMonitor.Email.OAuth;

public sealed record OAuthAccessToken(string Value, DateTimeOffset ExpiresAtUtc)
{
    public bool IsUsable(DateTimeOffset nowUtc, TimeSpan safetyMargin) =>
        !string.IsNullOrWhiteSpace(Value) && ExpiresAtUtc - nowUtc > safetyMargin;
}

public interface IAccessTokenProvider
{
    Task<OAuthAccessToken> GetAccessTokenAsync(CancellationToken cancellationToken);
}
