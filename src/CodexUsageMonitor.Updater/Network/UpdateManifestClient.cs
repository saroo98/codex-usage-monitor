using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexUsageMonitor.Updater.Manifest;

namespace CodexUsageMonitor.Updater.Network;

public sealed record ManifestFetchResult(UpdateManifestDocument? Manifest, string? EntityTag, bool NotModified);

public sealed class UpdateManifestClient
{
    private const int MaximumManifestBytes = 256 * 1024;
    private readonly HttpClient _httpClient;
    private readonly UpdateManifestValidator _validator;
    private readonly ManifestSignatureVerifier _signatureVerifier;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public UpdateManifestClient(
        HttpClient httpClient,
        UpdateManifestValidator validator,
        ManifestSignatureVerifier signatureVerifier)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _signatureVerifier = signatureVerifier ?? throw new ArgumentNullException(nameof(signatureVerifier));
    }

    public async Task<ManifestFetchResult> FetchAsync(
        Uri manifestUri,
        string? entityTag,
        CancellationToken cancellationToken)
    {
        ValidateHttps(manifestUri);
        using var request = new HttpRequestMessage(HttpMethod.Get, manifestUri);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("CodexUsageMonitor/1.0");
        if (!string.IsNullOrWhiteSpace(entityTag) && EntityTagHeaderValue.TryParse(entityTag, out var parsedTag))
        {
            request.Headers.IfNoneMatch.Add(parsedTag);
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        ValidateHttps(response.RequestMessage?.RequestUri ?? manifestUri);
        if (response.StatusCode is HttpStatusCode.NotModified)
        {
            return new ManifestFetchResult(null, response.Headers.ETag?.ToString() ?? entityTag, true);
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumManifestBytes)
        {
            throw new InvalidDataException("Update manifest is too large.");
        }

        var payload = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
        UpdateManifestDocument manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<UpdateManifestDocument>(payload, _jsonOptions)
                ?? throw new InvalidDataException("Update manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Update manifest JSON is invalid.", exception);
        }

        _validator.Validate(manifest);
        if (!_signatureVerifier.Verify(manifest))
        {
            throw new System.Security.Cryptography.CryptographicException("Update manifest signature is invalid.");
        }

        return new ManifestFetchResult(manifest, response.Headers.ETag?.ToString(), false);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > MaximumManifestBytes)
            {
                throw new InvalidDataException("Update manifest is too large.");
            }

            output.Write(buffer, 0, read);
        }
    }

    private static void ValidateHttps(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidDataException("Update endpoints must use HTTPS without embedded credentials.");
        }
    }
}
