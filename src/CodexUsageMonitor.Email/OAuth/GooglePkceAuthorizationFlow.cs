using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using CodexUsageMonitor.Core.Abstractions;

namespace CodexUsageMonitor.Email.OAuth;

public sealed class GooglePkceAuthorizationFlow : IGooglePkceAuthorizationFlow
{
    private static readonly Uri AuthorizationEndpoint = new("https://accounts.google.com/o/oauth2/v2/auth");
    private static readonly Uri TokenEndpoint = new("https://oauth2.googleapis.com/token");
    private readonly HttpClient _httpClient;
    private readonly OAuthTokenStore _store;
    private readonly IBrowserLauncher _browser;
    private readonly IClock _clock;

    public GooglePkceAuthorizationFlow(HttpClient httpClient, OAuthTokenStore store, IBrowserLauncher browser, IClock clock)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<OAuthTokenSet> ConnectAsync(
        string clientId,
        string tokenStoreKey,
        IReadOnlyList<string> scopes,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var port = ReserveLoopbackPort();
        var redirectUri = new Uri($"http://127.0.0.1:{port}/oauth2/callback/");
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri.AbsoluteUri);
        listener.Start();
        var authorizationUri = BuildAuthorizationUri(clientId, redirectUri, scopes, challenge, state);
        await _browser.OpenAsync(authorizationUri, cancellationToken).ConfigureAwait(false);

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

        var query = context.Request.QueryString;
        var returnedState = query["state"];
        var code = query["code"];
        var error = query["error"];
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
        var fields = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri.AbsoluteUri,
        };
        using var tokenResponse = await OAuthHttpProtocol.PostFormAsync(
            _httpClient,
            TokenEndpoint,
            fields,
            cancellationToken).ConfigureAwait(false);
        var tokens = OAuthHttpProtocol.ParseTokenSet(tokenResponse.RootElement, _clock.UtcNow);
        await _store.SaveAsync(tokenStoreKey, tokens, cancellationToken).ConfigureAwait(false);
        return tokens;
    }

    public static IReadOnlyList<string> GmailApiScopes { get; } =
        ["openid", "email", "https://www.googleapis.com/auth/gmail.send"];

    private static Uri BuildAuthorizationUri(
        string clientId,
        Uri redirectUri,
        IReadOnlyList<string> scopes,
        string challenge,
        string state)
    {
        var values = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["response_type"] = "code",
            ["scope"] = string.Join(' ', scopes),
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["access_type"] = "offline",
            ["prompt"] = "consent",
        };
        var query = string.Join('&', values.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new UriBuilder(AuthorizationEndpoint) { Query = query }.Uri;
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
        var payload = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=\"utf-8\"><title>{WebUtility.HtmlEncode(title)}</title><body style=\"font:16px Segoe UI,sans-serif;padding:40px;background:#11151b;color:#f4f7fb\"><h1>{WebUtility.HtmlEncode(title)}</h1><p>{WebUtility.HtmlEncode(body)}</p></body>");
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = payload.Length;
        await response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
        response.Close();
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
