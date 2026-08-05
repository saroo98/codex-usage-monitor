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

    public IEmailTransport? Resolve(EmailOutboxItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var email = _settings.Current.Email;
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
        var sender = email.SenderAddress!.Trim();
        return email.Provider switch
        {
            EmailProviderMode.GenericSmtp => BuildPasswordTransport(email, sender, fingerprint),
            EmailProviderMode.MicrosoftOAuth => BuildOAuthTransport(
                email,
                sender,
                fingerprint,
                "smtp.office365.com",
                587,
                new Uri($"https://login.microsoftonline.com/{Uri.EscapeDataString(email.OAuthTenant ?? "common")}/oauth2/v2.0/token"),
                MicrosoftDeviceCodeFlow.SmtpScopes),
            EmailProviderMode.GoogleOAuth => BuildOAuthTransport(
                email,
                sender,
                fingerprint,
                "smtp.gmail.com",
                587,
                new Uri("https://oauth2.googleapis.com/token"),
                GooglePkceAuthorizationFlow.SmtpScopes),
            _ => null,
        };
    }

    private CacheEntry? BuildPasswordTransport(EmailSettings email, string sender, string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(email.SmtpHost))
        {
            return null;
        }

        var reference = string.IsNullOrWhiteSpace(email.CredentialReference)
            ? EmailSecretKeyFactory.SmtpPassword(sender)
            : email.CredentialReference;
        var connection = new SmtpConnectionSettings(
            email.SmtpHost.Trim(),
            Math.Clamp(email.SmtpPort, 1, 65535),
            email.SmtpSecurity is not SmtpSecurityMode.None,
            string.IsNullOrWhiteSpace(email.SmtpUsername) ? sender : email.SmtpUsername.Trim(),
            reference,
            UseOAuth2: false);
        return new CacheEntry(
            fingerprint,
            new SmtpEmailTransport(connection, _secrets, null, _loggerFactory.CreateLogger<SmtpEmailTransport>()),
            null);
    }

    private CacheEntry? BuildOAuthTransport(
        EmailSettings email,
        string sender,
        string fingerprint,
        string smtpHost,
        int smtpPort,
        Uri tokenEndpoint,
        IReadOnlyList<string> scopes)
    {
        if (string.IsNullOrWhiteSpace(email.OAuthClientId))
        {
            return null;
        }

        var tokenKey = string.IsNullOrWhiteSpace(email.OAuthTokenReference)
            ? EmailSecretKeyFactory.OAuthTokens(email.Provider, sender, email.OAuthRegistrationId ?? email.OAuthClientId)
            : email.OAuthTokenReference;
        var provider = new RefreshingAccessTokenProvider(
            _httpClient,
            _tokens,
            new OAuthRefreshConfiguration(tokenEndpoint, email.OAuthClientId.Trim(), null, tokenKey, scopes),
            _clock);
        var connection = new SmtpConnectionSettings(
            smtpHost,
            smtpPort,
            UseTls: true,
            sender,
            tokenKey,
            UseOAuth2: true);
        return new CacheEntry(
            fingerprint,
            new SmtpEmailTransport(connection, _secrets, provider, _loggerFactory.CreateLogger<SmtpEmailTransport>()),
            provider);
    }

    private static string? Fingerprint(EmailSettings email)
    {
        if (email.Provider is EmailProviderMode.Disabled || string.IsNullOrWhiteSpace(email.SenderAddress))
        {
            return null;
        }

        return string.Join('|',
            email.Provider,
            email.SenderAddress.Trim().ToUpperInvariant(),
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

    private sealed record CacheEntry(string Fingerprint, IEmailTransport Transport, IDisposable? Lifetime) : IDisposable
    {
        public void Dispose() => Lifetime?.Dispose();
    }
}
