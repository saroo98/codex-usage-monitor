using System.Net.Http;
using CodexUsageMonitor.Core.Abstractions;
using CodexUsageMonitor.Core.Security;
using CodexUsageMonitor.Core.Settings;
using CodexUsageMonitor.Email.Models;
using CodexUsageMonitor.Email.OAuth;
using CodexUsageMonitor.Email.Security;
using CodexUsageMonitor.Email.Transport;
using CodexUsageMonitor.Persistence.Outbox;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.App.Services;

public sealed class EmailTransportFactory : IDisposable
{
    private readonly object _gate = new();
    private readonly ApplicationSettingsService _settings;
    private readonly ISecretStore _secrets;
    private readonly OAuthTokenStore _tokens;
    private readonly HttpClient _httpClient;
    private readonly IClock _clock;
    private readonly ILoggerFactory _loggerFactory;
    private CacheEntry? _cache;
    private bool _disposed;

    public EmailTransportFactory(
        ApplicationSettingsService settings,
        ISecretStore secrets,
        OAuthTokenStore tokens,
        HttpClient httpClient,
        IClock clock,
        ILoggerFactory loggerFactory)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _settings.Changed += OnSettingsChanged;
    }

    public ISelfNotificationSender? Resolve(EmailOutboxItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var email = _settings.Current.Email;
        if (!email.Enabled)
        {
            return null;
        }

        return Resolve(email);
    }

    public ISelfNotificationSender? ResolveForExplicitTest()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Resolve(_settings.Current.Email);
    }

    private ISelfNotificationSender? Resolve(EmailSettings email)
    {
        var fingerprint = Fingerprint(email);
        if (fingerprint is null)
        {
            return null;
        }

        lock (_gate)
        {
            if (_cache is not null && string.Equals(_cache.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return _cache.Transport;
            }

            _cache?.Dispose();
            _cache = Build(email, fingerprint);
            return _cache?.Transport;
        }
    }

    private CacheEntry? Build(EmailSettings email, string fingerprint)
    {
        var sender = (email.ConnectedAddress ?? email.SenderAddress)!.Trim();
        var account = EmailAccountIdentity.Create(sender);
        return email.Provider switch
        {
            EmailProviderMode.OtherSmtp => BuildPasswordTransport(email, account, fingerprint, protonBridge: false),
            EmailProviderMode.ProtonMailBridge => BuildPasswordTransport(email, account, fingerprint, protonBridge: true),
            EmailProviderMode.Microsoft365 => BuildMicrosoftGraphTransport(
                email,
                account,
                fingerprint,
                new Uri($"https://login.microsoftonline.com/{Uri.EscapeDataString(email.OAuthTenant ?? "common")}/oauth2/v2.0/token"),
                MicrosoftPkceAuthorizationFlow.GraphScopes),
            EmailProviderMode.Gmail => BuildGmailApiTransport(
                email,
                account,
                fingerprint,
                new Uri("https://oauth2.googleapis.com/token"),
                GooglePkceAuthorizationFlow.GmailApiScopes),
            _ => null,
        };
    }

    private CacheEntry? BuildPasswordTransport(
        EmailSettings email,
        EmailAccountIdentity account,
        string fingerprint,
        bool protonBridge)
    {
        if (string.IsNullOrWhiteSpace(email.SmtpHost))
        {
            return null;
        }

        var security = email.SmtpSecurity switch
        {
            SmtpSecurityMode.Tls => SmtpTransportSecurity.Tls,
            SmtpSecurityMode.StartTls or SmtpSecurityMode.Auto => SmtpTransportSecurity.StartTls,
            _ => SmtpTransportSecurity.None,
        };
        if (security is SmtpTransportSecurity.None)
        {
            return null;
        }

        var reference = string.IsNullOrWhiteSpace(email.CredentialReference)
            ? EmailSecretKeyFactory.SmtpPassword(account.Address)
            : email.CredentialReference;
        var userName = string.IsNullOrWhiteSpace(email.SmtpUsername) ? account.Address : email.SmtpUsername.Trim();
        var connection = protonBridge
            ? SmtpConnectionSettings.ForProtonBridge(
                email.SmtpHost.Trim(),
                Math.Clamp(email.SmtpPort, 1, 65535),
                security,
                userName,
                reference)
            : new SmtpConnectionSettings(
                email.SmtpHost.Trim(),
                Math.Clamp(email.SmtpPort, 1, 65535),
                UseTls: true,
                userName,
                reference,
                UseOAuth2: false)
            {
                Security = security,
            };
        return new CacheEntry(
            fingerprint,
            new SmtpEmailTransport(connection, account, _secrets, null, _loggerFactory.CreateLogger<SmtpEmailTransport>()),
            null);
    }

    private CacheEntry? BuildGmailApiTransport(
        EmailSettings email,
        EmailAccountIdentity account,
        string fingerprint,
        Uri tokenEndpoint,
        IReadOnlyList<string> scopes)
    {
        if (string.IsNullOrWhiteSpace(email.OAuthClientId))
        {
            return null;
        }

        var tokenKey = string.IsNullOrWhiteSpace(email.OAuthTokenReference)
            ? EmailSecretKeyFactory.OAuthTokens(email.Provider, account.Address, email.OAuthRegistrationId ?? email.OAuthClientId)
            : email.OAuthTokenReference;
        var provider = new RefreshingAccessTokenProvider(
            _httpClient,
            _tokens,
            new OAuthRefreshConfiguration(tokenEndpoint, email.OAuthClientId.Trim(), null, tokenKey, scopes),
            _clock);
        return new CacheEntry(
            fingerprint,
            new GmailApiSelfNotificationTransport(_httpClient, provider, account),
            provider);
    }

    private CacheEntry? BuildMicrosoftGraphTransport(
        EmailSettings email,
        EmailAccountIdentity account,
        string fingerprint,
        Uri tokenEndpoint,
        IReadOnlyList<string> scopes)
    {
        if (string.IsNullOrWhiteSpace(email.OAuthClientId))
        {
            return null;
        }

        var tokenKey = string.IsNullOrWhiteSpace(email.OAuthTokenReference)
            ? EmailSecretKeyFactory.OAuthTokens(email.Provider, account.Address, email.OAuthRegistrationId ?? email.OAuthClientId)
            : email.OAuthTokenReference;
        var provider = new RefreshingAccessTokenProvider(
            _httpClient,
            _tokens,
            new OAuthRefreshConfiguration(tokenEndpoint, email.OAuthClientId.Trim(), null, tokenKey, scopes),
            _clock);
        return new CacheEntry(
            fingerprint,
            new MicrosoftGraphSelfNotificationTransport(_httpClient, provider, account),
            provider);
    }

    private static string? Fingerprint(EmailSettings email)
    {
        if (email.Provider is EmailProviderMode.Off || string.IsNullOrWhiteSpace(email.ConnectedAddress ?? email.SenderAddress))
        {
            return null;
        }

        return string.Join('|',
            email.Provider,
            email.Enabled,
            email.ConnectedAddress?.Trim().ToUpperInvariant(),
            email.SenderAddress?.Trim().ToUpperInvariant(),
            email.SmtpHost?.Trim().ToUpperInvariant(),
            email.SmtpPort,
            email.SmtpSecurity is not SmtpSecurityMode.None,
            email.CredentialReference,
            email.OAuthClientId?.Trim(),
            email.OAuthTenant?.Trim(),
            email.OAuthTokenReference,
            email.OAuthRegistrationId);
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        lock (_gate)
        {
            _cache?.Dispose();
            _cache = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        lock (_gate)
        {
            _cache?.Dispose();
            _cache = null;
        }
    }

    private sealed record CacheEntry(string Fingerprint, ISelfNotificationSender Transport, IDisposable? Lifetime) : IDisposable
    {
        public void Dispose() => Lifetime?.Dispose();
    }
}
