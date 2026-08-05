using System.Security.Cryptography;
using System.Text.Json;
using CodexUsageMonitor.Core.Security;

namespace CodexUsageMonitor.Email.OAuth;

public sealed record OAuthTokenSet(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset ExpiresAtUtc,
    string TokenType,
    string? Scope);

public sealed class OAuthTokenStore
{
    private const int MaximumTokenPayloadBytes = 128 * 1024;
    private readonly ISecretStore _secrets;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public OAuthTokenStore(ISecretStore secrets)
    {
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
    }

    public async Task SaveAsync(string key, OAuthTokenSet tokens, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(tokens);
        var payload = JsonSerializer.SerializeToUtf8Bytes(tokens, _jsonOptions);
        if (payload.Length > MaximumTokenPayloadBytes)
        {
            CryptographicOperations.ZeroMemory(payload);
            throw new InvalidDataException("OAuth token response exceeds the accepted size.");
        }

        try
        {
            await _secrets.SetAsync(key, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public async Task<OAuthTokenSet?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        var payload = await _secrets.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (payload is null)
        {
            return null;
        }

        try
        {
            if (payload.Length is <= 0 or > MaximumTokenPayloadBytes)
            {
                throw new InvalidDataException("Stored OAuth token payload is invalid.");
            }

            return JsonSerializer.Deserialize<OAuthTokenSet>(payload, _jsonOptions)
                ?? throw new InvalidDataException("Stored OAuth token payload is empty.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        return _secrets.DeleteAsync(key, cancellationToken);
    }

    private static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }
    }
}
