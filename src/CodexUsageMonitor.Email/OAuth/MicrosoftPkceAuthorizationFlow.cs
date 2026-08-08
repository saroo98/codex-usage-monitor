using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using CodexUsageMonitor.Core.Abstractions;

namespace CodexUsageMonitor.Email.OAuth;

public interface IMicrosoftPkceAuthorizationFlow
{
    Task<OAuthTokenSet> ConnectAsync(
        string tenant,
        string clientId,
        IReadOnlyList<string> scopes,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public sealed class MicrosoftPkceAuthorizationFlow : IMicrosoftPkceAuthorizationFlow
{
    private readonly HttpClient _httpClient;
    private readonly IBrowserLauncher _browser;
    private readonly IClock _clock;

    public MicrosoftPkceAuthorizationFlow(HttpClient httpClient, IBrowserLauncher browser, IClock clock)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public static IReadOnlyList<string> GraphScopes { get; } =
        ["offline_access", "openid", "email", "User.Read", "Mail.Send"];

    public async Task<OAuthTokenSet> ConnectAsync(
        string tenant,
        string clientId,
        IReadOnlyList<string> scopes,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ValidateTenant(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(scopes);
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var port = ReserveLoopbackPort();
        var redirectUri = new Uri($"http://localhost:{port}/oauth2/callback/");
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri.AbsoluteUri);
        listener.Start();
        var authorizationEndpoint = new Uri($"https://login.microsoftonline.com/{Uri.EscapeDataString(tenant)}/oauth2/v2.0/authorize");
        var tokenEndpoint = new Uri($"https://login.microsoftonline.com/{Uri.EscapeDataString(tenant)}/oauth2/v2.0/token");
        await _browser.OpenAsync(
            BuildAuthorizationUri(authorizationEndpoint, clientId, redirectUri, scopes, challenge, state),
            cancellationToken).ConfigureAwait(false);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync().WaitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OAuthProtocolException("oauth.authorization_timeout", HttpStatusCode.RequestTimeout);
        }

        var returnedState = context.Request.QueryString["state"];
        var code = context.Request.QueryString["code"];
        var error = context.Request.QueryString["error"];
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(state),
                Encoding.UTF8.GetBytes(returnedState ?? string.Empty)))
        {
            await RespondAsync(context.Response, "Connection rejected", "The OAuth state did not match.").ConfigureAwait(false);
            throw new OAuthProtocolException("oauth.state_mismatch", HttpStatusCode.BadRequest);
        }

        if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(code))
        {
            await RespondAsync(context.Response, "Connection cancelled", "No credentials were stored.").ConfigureAwait(false);
            throw new OAuthProtocolException(error is "access_denied" ? "oauth.access_denied" : "oauth.authorization_failed", HttpStatusCode.BadRequest);
        }

        await RespondAsync(context.Response, "Connected", "You can close this tab and return to Codex Usage Monitor.").ConfigureAwait(false);
        using var tokenResponse = await OAuthHttpProtocol.PostFormAsync(
            _httpClient,
            tokenEndpoint,
            new Dictionary<string, string>
            {
                ["client_id"] = clientId.Trim(),
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = redirectUri.AbsoluteUri,
                ["scope"] = string.Join(' ', scopes),
            },
            cancellationToken).ConfigureAwait(false);
        return OAuthHttpProtocol.ParseTokenSet(tokenResponse.RootElement, _clock.UtcNow);
    }

    private static Uri BuildAuthorizationUri(
        Uri endpoint,
        string clientId,
        Uri redirectUri,
        IReadOnlyList<string> scopes,
        string challenge,
        string state)
    {
        var values = new Dictionary<string, string>
        {
            ["client_id"] = clientId.Trim(),
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["response_type"] = "code",
            ["response_mode"] = "query",
            ["scope"] = string.Join(' ', scopes),
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["prompt"] = "select_account",
        };
        var query = string.Join('&', values.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new UriBuilder(endpoint) { Query = query }.Uri;
    }

    private static void ValidateTenant(string tenant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        if (tenant.Length > 128 || tenant.Any(static character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '.')))
        {
            throw new ArgumentException("OAuth tenant is invalid.", nameof(tenant));
        }
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task RespondAsync(HttpListenerResponse response, string title, string body)
    {
        var payload = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=\"utf-8\"><title>{WebUtility.HtmlEncode(title)}</title><body><h1>{WebUtility.HtmlEncode(title)}</h1><p>{WebUtility.HtmlEncode(body)}</p></body>");
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = payload.Length;
        await response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
        response.Close();
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
