using System.Net.Http.Headers;
using System.Text.Json;
using CodexUsageMonitor.Email.Models;

namespace CodexUsageMonitor.Email.OAuth;

public interface IProviderEmailAccountIdentityResolver
{
    Task<EmailAccountIdentity> ResolveGoogleAsync(OAuthAccessToken token, CancellationToken cancellationToken);
    Task<EmailAccountIdentity> ResolveMicrosoftAsync(OAuthAccessToken token, CancellationToken cancellationToken);
}

public sealed class ProviderEmailAccountIdentityResolver : IProviderEmailAccountIdentityResolver
{
    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly Uri GoogleUserInfo = new("https://openidconnect.googleapis.com/v1/userinfo");
    private static readonly Uri MicrosoftProfile = new("https://graph.microsoft.com/v1.0/me?$select=mail,userPrincipalName");
    private readonly HttpClient _httpClient;

    public ProviderEmailAccountIdentityResolver(HttpClient httpClient) =>
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public async Task<EmailAccountIdentity> ResolveGoogleAsync(OAuthAccessToken token, CancellationToken cancellationToken)
    {
        using var document = await GetAsync(GoogleUserInfo, token, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (!root.TryGetProperty("email_verified", out var verified) || verified.ValueKind is not JsonValueKind.True)
        {
            throw new OAuthProtocolException("oauth.email_not_verified", System.Net.HttpStatusCode.OK);
        }

        return EmailAccountIdentity.Create(OAuthHttpProtocol.RequiredString(root, "email", 320));
    }

    public async Task<EmailAccountIdentity> ResolveMicrosoftAsync(OAuthAccessToken token, CancellationToken cancellationToken)
    {
        using var document = await GetAsync(MicrosoftProfile, token, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var address = OAuthHttpProtocol.OptionalString(root, "mail", 320)
            ?? OAuthHttpProtocol.RequiredString(root, "userPrincipalName", 320);
        return EmailAccountIdentity.Create(address);
    }

    private async Task<JsonDocument> GetAsync(Uri endpoint, OAuthAccessToken token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new OAuthProtocolException("oauth.identity_unavailable", response.StatusCode);
        }

        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw new OAuthProtocolException("oauth.response_too_large", response.StatusCode);
        }

        var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (payload.Length is <= 0 or > MaximumResponseBytes)
        {
            throw new OAuthProtocolException("oauth.invalid_identity_response", response.StatusCode);
        }

        try
        {
            return JsonDocument.Parse(payload);
        }
        catch (JsonException exception)
        {
            throw new OAuthProtocolException("oauth.invalid_json", response.StatusCode, exception);
        }
    }
}
