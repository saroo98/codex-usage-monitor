using System.ComponentModel;
using System.Net.Mail;
using System.Security.Cryptography;
using CodexUsageMonitor.Core.Security;
using CodexUsageMonitor.Core.Settings;
using CodexUsageMonitor.Email.OAuth;
using CodexUsageMonitor.Email.Security;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.App.Services;

public enum OAuthConnectionState
{
    NotConnected,
    Connected,
    ConnectedWithCleanupWarning,
    Unavailable,
}

public sealed record OAuthConnectionStatus(OAuthConnectionState State, string SafeMessageCode)
{
    public bool IsConnected => State is OAuthConnectionState.Connected or OAuthConnectionState.ConnectedWithCleanupWarning;

    public static OAuthConnectionStatus NotConnected { get; } = new(OAuthConnectionState.NotConnected, "email.oauth_not_connected");

    public static OAuthConnectionStatus Connected { get; } = new(OAuthConnectionState.Connected, "email.oauth_connected");
}


public sealed record MicrosoftOAuthPrompt(
    string UserCode,
    Uri VerificationUri,
    string? ProviderMessage,
    DateTimeOffset ExpiresAtUtc);

public sealed class OAuthConnectionService
{
    private static readonly TimeSpan GoogleAuthorizationTimeout = TimeSpan.FromMinutes(5);
    private readonly ApplicationSettingsService _settings;
    private readonly ISecretStore _secrets;
    private readonly IMicrosoftDeviceCodeFlow _microsoft;
    private readonly IGooglePkceAuthorizationFlow _google;
    private readonly IBrowserLauncher _browser;
    private readonly ILogger<OAuthConnectionService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OAuthConnectionService(
        ApplicationSettingsService settings,
        ISecretStore secrets,
        IMicrosoftDeviceCodeFlow microsoft,
        IGooglePkceAuthorizationFlow google,
        IBrowserLauncher browser,
        ILogger<OAuthConnectionService> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _microsoft = microsoft ?? throw new ArgumentNullException(nameof(microsoft));
        _google = google ?? throw new ArgumentNullException(nameof(google));
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<OAuthConnectionStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var email = _settings.Current.Email;
        var reference = ResolveTokenReference(email);
        if (reference is null)
        {
            return OAuthConnectionStatus.NotConnected;
        }

        byte[]? payload = null;
        try
        {
            payload = await _secrets.GetAsync(reference, cancellationToken).ConfigureAwait(false);
            return payload is { Length: > 0 } ? OAuthConnectionStatus.Connected : OAuthConnectionStatus.NotConnected;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            _logger.LogWarning(exception, "OAuth connection status could not be read.");
            return new OAuthConnectionStatus(OAuthConnectionState.Unavailable, "email.oauth_status_unavailable");
        }
        finally
        {
            if (payload is not null)
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }
    }

    public async Task<OAuthConnectionStatus> ConnectMicrosoftAsync(
        string senderAddress,
        string clientId,
        string? tenant,
        Func<MicrosoftOAuthPrompt, CancellationToken, Task> presentChallenge,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(presentChallenge);
        var normalizedTenant = string.IsNullOrWhiteSpace(tenant) ? "common" : tenant.Trim();
        return await ConnectAsync(
            EmailProviderMode.MicrosoftOAuth,
            senderAddress,
            clientId,
            normalizedTenant,
            async (tokenReference, token) =>
            {
                var challenge = await _microsoft.BeginAsync(
                    normalizedTenant,
                    clientId.Trim(),
                    MicrosoftDeviceCodeFlow.SmtpScopes,
                    token).ConfigureAwait(false);
                await presentChallenge(
                    new MicrosoftOAuthPrompt(
                        challenge.UserCode,
                        challenge.VerificationUri,
                        challenge.Message,
                        challenge.ExpiresAtUtc),
                    token).ConfigureAwait(false);
                await _browser.OpenAsync(challenge.VerificationUri, token).ConfigureAwait(false);
                await _microsoft.CompleteAsync(
                    challenge,
                    normalizedTenant,
                    clientId.Trim(),
                    tokenReference,
                    MicrosoftDeviceCodeFlow.SmtpScopes,
                    token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public Task<OAuthConnectionStatus> ConnectGoogleAsync(
        string senderAddress,
        string clientId,
        CancellationToken cancellationToken) =>
        ConnectAsync(
            EmailProviderMode.GoogleOAuth,
            senderAddress,
            clientId,
            tenant: null,
            async (tokenReference, token) =>
            {
                await _google.ConnectAsync(
                    clientId.Trim(),
                    clientSecret: null,
                    tokenReference,
                    GooglePkceAuthorizationFlow.SmtpScopes,
                    GoogleAuthorizationTimeout,
                    token).ConfigureAwait(false);
            },
            cancellationToken);

    public async Task<OAuthConnectionStatus> DisconnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = _settings.Current.Email;
            var reference = ResolveTokenReference(before);
            if (reference is null)
            {
                return OAuthConnectionStatus.NotConnected;
            }

            var previousPayload = await _secrets.GetAsync(reference, cancellationToken).ConfigureAwait(false);
            try
            {
                await _secrets.DeleteAsync(reference, cancellationToken).ConfigureAwait(false);
                try
                {
                    await _settings.UpdateAsync(settings => settings with
                    {
                        Email = settings.Email with
                        {
                            OAuthTokenReference = null,
                            OAuthRegistrationId = null,
                        },
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    if (previousPayload is { Length: > 0 })
                    {
                        await _secrets.SetAsync(reference, previousPayload, cancellationToken).ConfigureAwait(false);
                    }

                    throw;
                }
            }
            finally
            {
                if (previousPayload is not null)
                {
                    CryptographicOperations.ZeroMemory(previousPayload);
                }
            }

            return OAuthConnectionStatus.NotConnected;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteReferenceAsync(string? reference, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(reference))
        {
            await _secrets.DeleteAsync(reference.Trim(), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<OAuthConnectionStatus> ConnectAsync(
        EmailProviderMode provider,
        string senderAddress,
        string clientId,
        string? tenant,
        Func<string, CancellationToken, Task> authorize,
        CancellationToken cancellationToken)
    {
        var normalizedSender = NormalizeSender(senderAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        var normalizedClientId = clientId.Trim();
        if (normalizedClientId.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(clientId));
        }

        var registrationId = EmailSecretKeyFactory.OAuthRegistrationId(provider, normalizedClientId);
        var newReference = EmailSecretKeyFactory.OAuthTokens(provider, normalizedSender, registrationId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = _settings.Current.Email;
            var priorAtNewReference = await _secrets.GetAsync(newReference, cancellationToken).ConfigureAwait(false);
            try
            {
                await authorize(newReference, cancellationToken).ConfigureAwait(false);
                try
                {
                    await _settings.UpdateAsync(settings => settings with
                    {
                        Email = settings.Email with
                        {
                            Provider = provider,
                            SenderAddress = normalizedSender,
                            OAuthClientId = normalizedClientId,
                            OAuthTenant = provider is EmailProviderMode.MicrosoftOAuth ? tenant : settings.Email.OAuthTenant,
                            OAuthTokenReference = newReference,
                            OAuthRegistrationId = registrationId,
                            CredentialReference = null,
                        },
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await RestoreSecretAsync(newReference, priorAtNewReference, cancellationToken).ConfigureAwait(false);
                    throw;
                }
            }
            finally
            {
                if (priorAtNewReference is not null)
                {
                    CryptographicOperations.ZeroMemory(priorAtNewReference);
                }
            }

            var cleanupWarning = false;
            foreach (var obsoleteReference in new[] { before.OAuthTokenReference, before.CredentialReference }
                         .Where(reference => !string.IsNullOrWhiteSpace(reference) &&
                             !string.Equals(reference, newReference, StringComparison.Ordinal))
                         .Distinct(StringComparer.Ordinal))
            {
                try
                {
                    await _secrets.DeleteAsync(obsoleteReference!, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
                {
                    cleanupWarning = true;
                    _logger.LogWarning(exception, "An obsolete email credential reference could not be removed.");
                }
            }

            return cleanupWarning
                ? new OAuthConnectionStatus(OAuthConnectionState.ConnectedWithCleanupWarning, "email.oauth_connected_cleanup_pending")
                : OAuthConnectionStatus.Connected;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? ResolveTokenReference(EmailSettings email)
    {
        if (!string.IsNullOrWhiteSpace(email.OAuthTokenReference))
        {
            return email.OAuthTokenReference.Trim();
        }

        if (email.Provider is not (EmailProviderMode.MicrosoftOAuth or EmailProviderMode.GoogleOAuth) ||
            string.IsNullOrWhiteSpace(email.SenderAddress) ||
            string.IsNullOrWhiteSpace(email.OAuthClientId))
        {
            return null;
        }

        var registrationId = email.OAuthRegistrationId ??
            EmailSecretKeyFactory.OAuthRegistrationId(email.Provider, email.OAuthClientId);
        return EmailSecretKeyFactory.OAuthTokens(email.Provider, email.SenderAddress, registrationId);
    }

    private static string NormalizeSender(string senderAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(senderAddress);
        var trimmed = senderAddress.Trim();
        var parsed = new MailAddress(trimmed);
        if (!string.Equals(parsed.Address, trimmed, StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("The sender email address is invalid.");
        }

        return parsed.Address;
    }

    private async Task RestoreSecretAsync(
        string reference,
        byte[]? priorSecret,
        CancellationToken cancellationToken)
    {
        if (priorSecret is { Length: > 0 })
        {
            await _secrets.SetAsync(reference, priorSecret, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _secrets.DeleteAsync(reference, cancellationToken).ConfigureAwait(false);
        }
    }
}
