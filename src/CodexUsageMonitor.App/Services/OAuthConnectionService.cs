using System.ComponentModel;
using System.Reflection;
using System.Security.Cryptography;
using CodexUsageMonitor.Core.Security;
using CodexUsageMonitor.Core.Settings;
using CodexUsageMonitor.Email.Models;
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

public sealed record OAuthConnectionStatus(
    OAuthConnectionState State,
    string SafeMessageCode,
    string? ConnectedAddress = null)
{
    public bool IsConnected => State is OAuthConnectionState.Connected or OAuthConnectionState.ConnectedWithCleanupWarning;
    public static OAuthConnectionStatus NotConnected { get; } = new(OAuthConnectionState.NotConnected, "email.oauth_not_connected");
}

public sealed record EmailProviderRegistrations(
    string? GoogleClientId,
    string? MicrosoftClientId,
    string MicrosoftTenant = "common")
{
    public bool GoogleAvailable => !string.IsNullOrWhiteSpace(GoogleClientId);
    public bool MicrosoftAvailable => !string.IsNullOrWhiteSpace(MicrosoftClientId);

    public static EmailProviderRegistrations FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal);
        return new EmailProviderRegistrations(
            Read("GoogleOAuthClientId"),
            Read("MicrosoftOAuthClientId"),
            Read("MicrosoftOAuthTenant") ?? "common");

        string? Read(string key) => metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
    }
}

public sealed class OAuthConnectionService
{
    private static readonly TimeSpan AuthorizationTimeout = TimeSpan.FromMinutes(5);
    private static readonly Uri GoogleRevokeEndpoint = new("https://oauth2.googleapis.com/revoke");
    private readonly ApplicationSettingsService _settings;
    private readonly ISecretStore _secrets;
    private readonly OAuthTokenStore _tokens;
    private readonly EmailProviderRegistrations _registrations;
    private readonly IMicrosoftPkceAuthorizationFlow _microsoft;
    private readonly IGooglePkceAuthorizationFlow _google;
    private readonly IProviderEmailAccountIdentityResolver _identities;
    private readonly HttpClient _httpClient;
    private readonly ILogger<OAuthConnectionService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OAuthConnectionService(
        ApplicationSettingsService settings,
        ISecretStore secrets,
        OAuthTokenStore tokens,
        EmailProviderRegistrations registrations,
        IMicrosoftPkceAuthorizationFlow microsoft,
        IGooglePkceAuthorizationFlow google,
        IProviderEmailAccountIdentityResolver identities,
        HttpClient httpClient,
        ILogger<OAuthConnectionService> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _registrations = registrations ?? throw new ArgumentNullException(nameof(registrations));
        _microsoft = microsoft ?? throw new ArgumentNullException(nameof(microsoft));
        _google = google ?? throw new ArgumentNullException(nameof(google));
        _identities = identities ?? throw new ArgumentNullException(nameof(identities));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public EmailProviderRegistrations Registrations => _registrations;

    public async Task<OAuthConnectionStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var email = _settings.Current.Email;
        if (email.Provider is not (EmailProviderMode.Gmail or EmailProviderMode.Microsoft365) ||
            string.IsNullOrWhiteSpace(email.ConnectedAddress) ||
            string.IsNullOrWhiteSpace(email.OAuthTokenReference))
        {
            return OAuthConnectionStatus.NotConnected;
        }

        byte[]? payload = null;
        try
        {
            payload = await _secrets.GetAsync(email.OAuthTokenReference, cancellationToken).ConfigureAwait(false);
            return payload is { Length: > 0 }
                ? new OAuthConnectionStatus(OAuthConnectionState.Connected, "email.oauth_connected", email.ConnectedAddress)
                : OAuthConnectionStatus.NotConnected;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            _logger.LogWarning(exception, "OAuth connection status could not be read.");
            return new OAuthConnectionStatus(OAuthConnectionState.Unavailable, "email.oauth_status_unavailable");
        }
        finally
        {
            if (payload is not null) CryptographicOperations.ZeroMemory(payload);
        }
    }

    public Task<OAuthConnectionStatus> ConnectGoogleAsync(CancellationToken cancellationToken)
    {
        if (!_registrations.GoogleAvailable)
        {
            return Task.FromResult(new OAuthConnectionStatus(OAuthConnectionState.Unavailable, "email.google_registration_unavailable"));
        }

        return ConnectAsync(
            EmailProviderMode.Gmail,
            _registrations.GoogleClientId!,
            tenant: null,
            async (temporaryReference, token) => await _google.ConnectAsync(
                _registrations.GoogleClientId!,
                temporaryReference,
                GooglePkceAuthorizationFlow.GmailApiScopes,
                AuthorizationTimeout,
                token).ConfigureAwait(false),
            (access, token) => _identities.ResolveGoogleAsync(access, token),
            cancellationToken);
    }

    public Task<OAuthConnectionStatus> ConnectMicrosoftAsync(CancellationToken cancellationToken)
    {
        if (!_registrations.MicrosoftAvailable)
        {
            return Task.FromResult(new OAuthConnectionStatus(OAuthConnectionState.Unavailable, "email.microsoft_registration_unavailable"));
        }

        var tenant = string.IsNullOrWhiteSpace(_registrations.MicrosoftTenant) ? "common" : _registrations.MicrosoftTenant.Trim();
        return ConnectAsync(
            EmailProviderMode.Microsoft365,
            _registrations.MicrosoftClientId!,
            tenant,
            (_, token) => _microsoft.ConnectAsync(
                tenant,
                _registrations.MicrosoftClientId!,
                MicrosoftPkceAuthorizationFlow.GraphScopes,
                AuthorizationTimeout,
                token),
            (access, token) => _identities.ResolveMicrosoftAsync(access, token),
            cancellationToken);
    }

    public async Task<OAuthConnectionStatus> DisconnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = _settings.Current.Email;
            if (string.IsNullOrWhiteSpace(before.OAuthTokenReference))
            {
                return OAuthConnectionStatus.NotConnected;
            }

            var reference = before.OAuthTokenReference.Trim();
            var priorPayload = await _secrets.GetAsync(reference, cancellationToken).ConfigureAwait(false);
            OAuthTokenSet? priorTokens = null;
            try
            {
                priorTokens = await _tokens.ReadAsync(reference, cancellationToken).ConfigureAwait(false);
                await _secrets.DeleteAsync(reference, cancellationToken).ConfigureAwait(false);
                try
                {
                    await _settings.UpdateAsync(settings => settings with
                    {
                        Email = settings.Email with
                        {
                            Enabled = false,
                            ConnectedAddress = null,
                            OAuthClientId = null,
                            OAuthTenant = "common",
                            OAuthTokenReference = null,
                            OAuthRegistrationId = null,
                        },
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    if (priorPayload is { Length: > 0 })
                    {
                        await _secrets.SetAsync(reference, priorPayload, cancellationToken).ConfigureAwait(false);
                    }

                    throw;
                }
            }
            finally
            {
                if (priorPayload is not null) CryptographicOperations.ZeroMemory(priorPayload);
            }

            var cleanupWarning = before.Provider is EmailProviderMode.Gmail && priorTokens is not null &&
                !await TryRevokeGoogleAsync(priorTokens, cancellationToken).ConfigureAwait(false);
            return cleanupWarning
                ? new OAuthConnectionStatus(OAuthConnectionState.ConnectedWithCleanupWarning, "email.oauth_disconnected_revoke_pending")
                : OAuthConnectionStatus.NotConnected;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task DeleteReferenceAsync(string? reference, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(reference)
            ? Task.CompletedTask
            : _secrets.DeleteAsync(reference.Trim(), cancellationToken);

    private async Task<OAuthConnectionStatus> ConnectAsync(
        EmailProviderMode provider,
        string clientId,
        string? tenant,
        Func<string, CancellationToken, Task<OAuthTokenSet>> authorize,
        Func<OAuthAccessToken, CancellationToken, Task<EmailAccountIdentity>> resolveIdentity,
        CancellationToken cancellationToken)
    {
        var temporaryReference = $"CodexUsageMonitor.Email.oauth-temporary.{Guid.NewGuid():N}";
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = _settings.Current.Email;
            OAuthTokenSet tokens;
            try
            {
                tokens = await authorize(temporaryReference, cancellationToken).ConfigureAwait(false);
                var account = await resolveIdentity(
                    new OAuthAccessToken(tokens.AccessToken, tokens.ExpiresAtUtc),
                    cancellationToken).ConfigureAwait(false);
                var registrationId = EmailSecretKeyFactory.OAuthRegistrationId(provider, clientId);
                var stableReference = EmailSecretKeyFactory.OAuthTokens(provider, account.Address, registrationId);
                var priorAtStableReference = await _secrets.GetAsync(stableReference, cancellationToken).ConfigureAwait(false);
                try
                {
                    await _tokens.SaveAsync(stableReference, tokens, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await _settings.UpdateAsync(settings => settings with
                        {
                            Email = settings.Email with
                            {
                                Provider = provider,
                                Enabled = false,
                                ConnectedAddress = account.Address,
                                SenderAddress = null,
                                Recipients = [],
                                OAuthClientId = clientId.Trim(),
                                OAuthTenant = provider is EmailProviderMode.Microsoft365 ? tenant : "common",
                                OAuthTokenReference = stableReference,
                                OAuthRegistrationId = registrationId,
                                CredentialReference = null,
                            },
                        }, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        await RestoreSecretAsync(stableReference, priorAtStableReference, cancellationToken).ConfigureAwait(false);
                        throw;
                    }
                }
                finally
                {
                    if (priorAtStableReference is not null) CryptographicOperations.ZeroMemory(priorAtStableReference);
                }

                var cleanupWarning = await DeleteObsoleteReferencesAsync(
                    [before.OAuthTokenReference, before.CredentialReference, temporaryReference],
                    stableReference,
                    cancellationToken).ConfigureAwait(false);
                return new OAuthConnectionStatus(
                    cleanupWarning ? OAuthConnectionState.ConnectedWithCleanupWarning : OAuthConnectionState.Connected,
                    cleanupWarning ? "email.oauth_connected_cleanup_pending" : "email.oauth_connected",
                    account.Address);
            }
            finally
            {
                try
                {
                    await _secrets.DeleteAsync(temporaryReference, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
                {
                    _logger.LogWarning(exception, "A temporary OAuth credential could not be removed.");
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> DeleteObsoleteReferencesAsync(
        IEnumerable<string?> references,
        string keepReference,
        CancellationToken cancellationToken)
    {
        var warning = false;
        foreach (var reference in references.Where(static value => !string.IsNullOrWhiteSpace(value))
                     .Select(static value => value!.Trim())
                     .Where(value => !string.Equals(value, keepReference, StringComparison.Ordinal))
                     .Distinct(StringComparer.Ordinal))
        {
            try
            {
                await _secrets.DeleteAsync(reference, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
            {
                warning = true;
                _logger.LogWarning(exception, "An obsolete email credential reference could not be removed.");
            }
        }

        return warning;
    }

    private async Task<bool> TryRevokeGoogleAsync(OAuthTokenSet tokens, CancellationToken cancellationToken)
    {
        var token = tokens.RefreshToken ?? tokens.AccessToken;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, GoogleRevokeEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }),
            };
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
        {
            _logger.LogWarning(exception, "Google authorization could not be revoked remotely; local tokens were removed.");
            return false;
        }
    }

    private async Task RestoreSecretAsync(string reference, byte[]? priorSecret, CancellationToken cancellationToken)
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
