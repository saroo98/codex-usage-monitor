using System.Security.Cryptography;
using System.Text;
using CodexUsageMonitor.Core.Settings;

namespace CodexUsageMonitor.Email.Security;

public static class EmailSecretKeyFactory
{
    private const string Prefix = "CodexUsageMonitor.Email.v1";

    public static string SmtpPassword(string senderAddress) => Create("smtp-password", senderAddress);

    public static string OAuthTokens(EmailProviderMode provider, string senderAddress) =>
        OAuthTokens(provider, senderAddress, registrationId: null);

    public static string OAuthTokens(EmailProviderMode provider, string senderAddress, string? registrationId) =>
        Create(provider switch
        {
            EmailProviderMode.MicrosoftOAuth => "microsoft-oauth",
            EmailProviderMode.GoogleOAuth => "google-oauth",
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        }, senderAddress, registrationId);

    public static string OAuthRegistrationId(EmailProviderMode provider, string clientId)
    {
        if (provider is not (EmailProviderMode.MicrosoftOAuth or EmailProviderMode.GoogleOAuth))
        {
            throw new ArgumentOutOfRangeException(nameof(provider));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        return Digest($"{provider}|{clientId.Trim()}", 16);
    }

    private static string Create(string purpose, string senderAddress, string? registrationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(senderAddress);
        var normalized = senderAddress.Trim().ToUpperInvariant();
        var registration = string.IsNullOrWhiteSpace(registrationId) ? string.Empty : $"|{registrationId.Trim()}";
        return $"{Prefix}.{purpose}.{Digest(normalized + registration, 16)}";
    }

    private static string Digest(string value, int byteCount)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            var digest = SHA256.HashData(bytes);
            return Convert.ToHexStringLower(digest.AsSpan(0, byteCount));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
