namespace CodexUsageMonitor.Email.OAuth;

public interface IMicrosoftDeviceCodeFlow
{
    Task<DeviceCodeChallenge> BeginAsync(
        string tenant,
        string clientId,
        IReadOnlyList<string> scopes,
        CancellationToken cancellationToken);

    Task<OAuthTokenSet> CompleteAsync(
        DeviceCodeChallenge challenge,
        string tenant,
        string clientId,
        string tokenStoreKey,
        IReadOnlyList<string> scopes,
        CancellationToken cancellationToken);
}

public interface IGooglePkceAuthorizationFlow
{
    Task<OAuthTokenSet> ConnectAsync(
        string clientId,
        string? clientSecret,
        string tokenStoreKey,
        IReadOnlyList<string> scopes,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
