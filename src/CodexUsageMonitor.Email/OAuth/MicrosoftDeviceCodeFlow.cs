using System.Net;
using System.Text.Json;
using CodexUsageMonitor.Core.Abstractions;

namespace CodexUsageMonitor.Email.OAuth;

public sealed record DeviceCodeChallenge(
    string UserCode,
    Uri VerificationUri,
    string? Message,
    DateTimeOffset ExpiresAtUtc,
    TimeSpan PollInterval,
    string DeviceCode);

public sealed class MicrosoftDeviceCodeFlow : IMicrosoftDeviceCodeFlow
{
    private readonly HttpClient _httpClient;
    private readonly OAuthTokenStore _store;
    private readonly IClock _clock;
    private readonly IAsyncDelay _delay;

    public MicrosoftDeviceCodeFlow(HttpClient httpClient, OAuthTokenStore store, IClock clock, IAsyncDelay delay)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
    }

    public async Task<DeviceCodeChallenge> BeginAsync(
        string tenant,
        string clientId,
        IReadOnlyList<string> scopes,
        CancellationToken cancellationToken)
    {
        ValidateSegment(tenant, nameof(tenant));
        ValidateClientId(clientId);
        var endpoint = new Uri($"https://login.microsoftonline.com/{Uri.EscapeDataString(tenant)}/oauth2/v2.0/devicecode");
        using var response = await OAuthHttpProtocol.PostFormAsync(
            _httpClient,
            endpoint,
            new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["scope"] = string.Join(' ', scopes),
            },
            cancellationToken).ConfigureAwait(false);
        var root = response.RootElement;
        var deviceCode = OAuthHttpProtocol.RequiredString(root, "device_code", 4096);
        var userCode = OAuthHttpProtocol.RequiredString(root, "user_code", 128);
        var verification = OAuthHttpProtocol.RequiredString(root, "verification_uri", 2048);
        var expiresIn = root.TryGetProperty("expires_in", out var expiresElement) && expiresElement.TryGetInt32(out var expires)
            ? Math.Clamp(expires, 60, 1800)
            : 900;
        var interval = root.TryGetProperty("interval", out var intervalElement) && intervalElement.TryGetInt32(out var seconds)
            ? Math.Clamp(seconds, 2, 30)
            : 5;
        var message = OAuthHttpProtocol.OptionalString(root, "message", 4096);
        return new DeviceCodeChallenge(
            userCode,
            new Uri(verification),
            message,
            _clock.UtcNow.AddSeconds(expiresIn),
            TimeSpan.FromSeconds(interval),
            deviceCode);
    }

    public async Task<OAuthTokenSet> CompleteAsync(
        DeviceCodeChallenge challenge,
        string tenant,
        string clientId,
        string tokenStoreKey,
        IReadOnlyList<string> scopes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        ValidateSegment(tenant, nameof(tenant));
        ValidateClientId(clientId);
        var endpoint = new Uri($"https://login.microsoftonline.com/{Uri.EscapeDataString(tenant)}/oauth2/v2.0/token");
        var interval = challenge.PollInterval;
        while (_clock.UtcNow < challenge.ExpiresAtUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _delay.DelayAsync(interval, cancellationToken).ConfigureAwait(false);
            try
            {
                using var response = await OAuthHttpProtocol.PostFormAsync(
                    _httpClient,
                    endpoint,
                    new Dictionary<string, string>
                    {
                        ["client_id"] = clientId,
                        ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                        ["device_code"] = challenge.DeviceCode,
                        ["scope"] = string.Join(' ', scopes),
                    },
                    cancellationToken).ConfigureAwait(false);
                var tokens = OAuthHttpProtocol.ParseTokenSet(response.RootElement, _clock.UtcNow);
                await _store.SaveAsync(tokenStoreKey, tokens, cancellationToken).ConfigureAwait(false);
                return tokens;
            }
            catch (OAuthProtocolException exception) when (exception.SafeErrorCode is "oauth.authorization_pending")
            {
            }
            catch (OAuthProtocolException exception) when (exception.SafeErrorCode is "oauth.slow_down")
            {
                interval += TimeSpan.FromSeconds(5);
            }
        }

        throw new OAuthProtocolException("oauth.expired_token", HttpStatusCode.RequestTimeout);
    }

    public static IReadOnlyList<string> SmtpScopes { get; } =
        ["offline_access", "openid", "email", "https://outlook.office.com/SMTP.Send"];

    private static void ValidateSegment(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 128 || value.Any(static character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '.')))
        {
            throw new ArgumentException("OAuth tenant is invalid.", parameterName);
        }
    }

    private static void ValidateClientId(string clientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        if (clientId.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(clientId));
        }
    }
}
