using CodexUsageMonitor.Core.Abstractions;

namespace CodexUsageMonitor.Email.OAuth;

public sealed record OAuthRefreshConfiguration(
    Uri TokenEndpoint,
    string ClientId,
    string? ClientSecret,
    string TokenStoreKey,
    IReadOnlyList<string> Scopes);

public sealed class RefreshingAccessTokenProvider : IAccessTokenProvider, IDisposable
{
    private static readonly TimeSpan SafetyMargin = TimeSpan.FromMinutes(5);
    private readonly HttpClient _httpClient;
    private readonly OAuthTokenStore _store;
    private readonly OAuthRefreshConfiguration _configuration;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private bool _disposed;

    public RefreshingAccessTokenProvider(
        HttpClient httpClient,
        OAuthTokenStore store,
        OAuthRefreshConfiguration configuration,
        IClock clock)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<OAuthAccessToken> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var stored = await _store.ReadAsync(_configuration.TokenStoreKey, cancellationToken).ConfigureAwait(false)
            ?? throw new OAuthProtocolException("oauth.not_connected", System.Net.HttpStatusCode.Unauthorized);
        if (new OAuthAccessToken(stored.AccessToken, stored.ExpiresAtUtc).IsUsable(_clock.UtcNow, SafetyMargin))
        {
            return new OAuthAccessToken(stored.AccessToken, stored.ExpiresAtUtc);
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            stored = await _store.ReadAsync(_configuration.TokenStoreKey, cancellationToken).ConfigureAwait(false)
                ?? throw new OAuthProtocolException("oauth.not_connected", System.Net.HttpStatusCode.Unauthorized);
            if (new OAuthAccessToken(stored.AccessToken, stored.ExpiresAtUtc).IsUsable(_clock.UtcNow, SafetyMargin))
            {
                return new OAuthAccessToken(stored.AccessToken, stored.ExpiresAtUtc);
            }

            if (string.IsNullOrWhiteSpace(stored.RefreshToken))
            {
                throw new OAuthProtocolException("oauth.refresh_token_missing", System.Net.HttpStatusCode.Unauthorized);
            }

            var fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["client_id"] = _configuration.ClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = stored.RefreshToken,
            };
            if (!string.IsNullOrWhiteSpace(_configuration.ClientSecret))
            {
                fields["client_secret"] = _configuration.ClientSecret;
            }

            if (_configuration.Scopes.Count > 0)
            {
                fields["scope"] = string.Join(' ', _configuration.Scopes);
            }

            using var response = await OAuthHttpProtocol.PostFormAsync(
                _httpClient,
                _configuration.TokenEndpoint,
                fields,
                cancellationToken).ConfigureAwait(false);
            var refreshed = OAuthHttpProtocol.ParseTokenSet(response.RootElement, _clock.UtcNow, stored.RefreshToken);
            await _store.SaveAsync(_configuration.TokenStoreKey, refreshed, cancellationToken).ConfigureAwait(false);
            return new OAuthAccessToken(refreshed.AccessToken, refreshed.ExpiresAtUtc);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshLock.Dispose();
    }
}
