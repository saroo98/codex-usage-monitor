using System.Net;
using System.Text.Json;

namespace CodexUsageMonitor.Email.OAuth;

internal static class OAuthHttpProtocol
{
    internal const int MaximumResponseBytes = 256 * 1024;

    internal static async Task<JsonDocument> PostFormAsync(
        HttpClient httpClient,
        Uri endpoint,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(fields),
        };
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var payload = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException exception)
        {
            throw new OAuthProtocolException("oauth.invalid_json", response.StatusCode, exception);
        }

        if (!response.IsSuccessStatusCode)
        {
            using (document)
            {
                var root = document.RootElement;
                var code = root.TryGetProperty("error", out var error) && error.ValueKind is JsonValueKind.String
                    ? error.GetString()
                    : null;
                throw new OAuthProtocolException(NormalizeError(code), response.StatusCode);
            }
        }

        return document;
    }

    internal static OAuthTokenSet ParseTokenSet(JsonElement root, DateTimeOffset nowUtc, string? retainedRefreshToken = null)
    {
        var accessToken = RequiredString(root, "access_token", 64 * 1024);
        var tokenType = OptionalString(root, "token_type", 64) ?? "Bearer";
        var refreshToken = OptionalString(root, "refresh_token", 64 * 1024) ?? retainedRefreshToken;
        var scope = OptionalString(root, "scope", 16 * 1024);
        var expiresIn = root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds)
            ? Math.Clamp(seconds, 60, 86_400)
            : 3600;
        return new OAuthTokenSet(accessToken, refreshToken, nowUtc.AddSeconds(expiresIn), tokenType, scope);
    }

    internal static string RequiredString(JsonElement root, string property, int maximumLength)
    {
        var value = OptionalString(root, property, maximumLength);
        return string.IsNullOrWhiteSpace(value)
            ? throw new OAuthProtocolException($"oauth.missing_{property}", HttpStatusCode.OK)
            : value;
    }

    internal static string? OptionalString(JsonElement root, string property, int maximumLength)
    {
        if (!root.TryGetProperty(property, out var element) || element.ValueKind is not JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString();
        if (value is null || value.Length > maximumLength)
        {
            throw new OAuthProtocolException($"oauth.invalid_{property}", HttpStatusCode.OK);
        }

        return value;
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw new OAuthProtocolException("oauth.response_too_large", response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaximumResponseBytes)
            {
                throw new OAuthProtocolException("oauth.response_too_large", response.StatusCode);
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static string NormalizeError(string? error) => error switch
    {
        "authorization_pending" => "oauth.authorization_pending",
        "slow_down" => "oauth.slow_down",
        "access_denied" => "oauth.access_denied",
        "expired_token" => "oauth.expired_token",
        "invalid_grant" => "oauth.invalid_grant",
        "invalid_client" => "oauth.invalid_client",
        _ => "oauth.request_failed",
    };
}

public sealed class OAuthProtocolException : Exception
{
    public OAuthProtocolException(string safeErrorCode, HttpStatusCode statusCode, Exception? innerException = null)
        : base(safeErrorCode, innerException)
    {
        SafeErrorCode = safeErrorCode;
        StatusCode = statusCode;
    }

    public string SafeErrorCode { get; }

    public HttpStatusCode StatusCode { get; }
}
