namespace CodexUsageMonitor.Email.OAuth;

public interface IGooglePkceAuthorizationFlow
{
    Task<OAuthTokenSet> ConnectAsync(
        string clientId,
        string tokenStoreKey,
        IReadOnlyList<string> scopes,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
