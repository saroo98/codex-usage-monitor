using System.Security.Cryptography;
using System.Text;
using System.Net;
using System.Net.Http;
using CodexUsageMonitor.App.Services;
using CodexUsageMonitor.Core.Security;
using CodexUsageMonitor.Core.Settings;
using CodexUsageMonitor.Email.OAuth;
using CodexUsageMonitor.Email.Security;
using CodexUsageMonitor.Email.Models;
using CodexUsageMonitor.Persistence.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexUsageMonitor.UiTests;

[TestClass]
public sealed class OAuthConnectionServiceTests
{
    [TestMethod]
    public async Task GoogleConnectPersistsExplicitTokenReferenceAndRemovesObsoletePassword()
    {
        var smtpReference = EmailSecretKeyFactory.SmtpPassword("sender@example.com");
        var initial = BaseSettings() with
        {
            Email = BaseSettings().Email with { CredentialReference = smtpReference },
        };
        var settingsStore = new MemorySettingsStore(initial);
        var settings = new ApplicationSettingsService(settingsStore);
        await settings.InitializeAsync(CancellationToken.None);
        var secrets = new MemorySecretStore();
        await secrets.SetAsync(smtpReference, Encoding.UTF8.GetBytes("app-password"), CancellationToken.None);
        var tokenStore = new OAuthTokenStore(secrets);
        var service = new OAuthConnectionService(
            settings,
            secrets,
            tokenStore,
            new EmailProviderRegistrations("google-client-id", "microsoft-client-id"),
            new FakeMicrosoftFlow(),
            new FakeGoogleFlow(tokenStore),
            new FakeIdentityResolver("sender@example.com"),
            new HttpClient(new NoOpHandler()),
            NullLogger<OAuthConnectionService>.Instance);

        var status = await service.ConnectGoogleAsync(CancellationToken.None);

        Assert.AreEqual(OAuthConnectionState.Connected, status.State);
        Assert.AreEqual(EmailProviderMode.Gmail, settings.Current.Email.Provider);
        Assert.IsNotNull(settings.Current.Email.OAuthTokenReference);
        Assert.IsNotNull(settings.Current.Email.OAuthRegistrationId);
        Assert.IsNull(settings.Current.Email.CredentialReference);
        Assert.IsNull(secrets.Copy(smtpReference));
        Assert.IsNotNull(secrets.Copy(settings.Current.Email.OAuthTokenReference!));
    }

    [TestMethod]
    public async Task ConnectRestoresPriorTokenWhenSettingsPersistenceFails()
    {
        var registration = EmailSecretKeyFactory.OAuthRegistrationId(
            EmailProviderMode.Gmail,
            "google-client-id");
        var reference = EmailSecretKeyFactory.OAuthTokens(
            EmailProviderMode.Gmail,
            "sender@example.com",
            registration);
        var initial = BaseSettings() with
        {
            Email = BaseSettings().Email with
            {
                Provider = EmailProviderMode.Gmail,
                ConnectedAddress = "sender@example.com",
                OAuthClientId = "google-client-id",
                OAuthRegistrationId = registration,
                OAuthTokenReference = reference,
            },
        };
        var settingsStore = new MemorySettingsStore(initial) { FailSaves = true };
        var settings = new ApplicationSettingsService(settingsStore);
        await settings.InitializeAsync(CancellationToken.None);
        var secrets = new MemorySecretStore();
        var prior = Encoding.UTF8.GetBytes("prior-token-payload");
        await secrets.SetAsync(reference, prior, CancellationToken.None);
        var tokenStore = new OAuthTokenStore(secrets);
        var service = new OAuthConnectionService(
            settings,
            secrets,
            tokenStore,
            new EmailProviderRegistrations("google-client-id", "microsoft-client-id"),
            new FakeMicrosoftFlow(),
            new FakeGoogleFlow(tokenStore),
            new FakeIdentityResolver("sender@example.com"),
            new HttpClient(new NoOpHandler()),
            NullLogger<OAuthConnectionService>.Instance);

        await Assert.ThrowsAsync<IOException>(() => service.ConnectGoogleAsync(CancellationToken.None));

        CollectionAssert.AreEqual(prior, secrets.Copy(reference)!);
        Assert.AreEqual(reference, settings.Current.Email.OAuthTokenReference);
    }

    [TestMethod]
    public async Task DisconnectRestoresTokenWhenSettingsPersistenceFails()
    {
        var registration = EmailSecretKeyFactory.OAuthRegistrationId(
            EmailProviderMode.Gmail,
            "google-client-id");
        var reference = EmailSecretKeyFactory.OAuthTokens(
            EmailProviderMode.Gmail,
            "sender@example.com",
            registration);
        var initial = BaseSettings() with
        {
            Email = BaseSettings().Email with
            {
                Provider = EmailProviderMode.Gmail,
                ConnectedAddress = "sender@example.com",
                OAuthClientId = "google-client-id",
                OAuthRegistrationId = registration,
                OAuthTokenReference = reference,
            },
        };
        var settingsStore = new MemorySettingsStore(initial) { FailSaves = true };
        var settings = new ApplicationSettingsService(settingsStore);
        await settings.InitializeAsync(CancellationToken.None);
        var secrets = new MemorySecretStore();
        var tokenStore = new OAuthTokenStore(secrets);
        await tokenStore.SaveAsync(reference, Tokens(), CancellationToken.None);
        var prior = secrets.Copy(reference)!;
        var service = new OAuthConnectionService(
            settings,
            secrets,
            tokenStore,
            new EmailProviderRegistrations("google-client-id", "microsoft-client-id"),
            new FakeMicrosoftFlow(),
            new FakeGoogleFlow(tokenStore),
            new FakeIdentityResolver("sender@example.com"),
            new HttpClient(new NoOpHandler()),
            NullLogger<OAuthConnectionService>.Instance);

        await Assert.ThrowsAsync<IOException>(() => service.DisconnectAsync(CancellationToken.None));

        CollectionAssert.AreEqual(prior, secrets.Copy(reference)!);
        Assert.AreEqual(reference, settings.Current.Email.OAuthTokenReference);
    }

    private static AppSettings BaseSettings() => new()
    {
        Email = new EmailSettings
        {
            Provider = EmailProviderMode.OtherSmtp,
            SenderAddress = "sender@example.com",
            Recipients = [],
            SmtpHost = "smtp.example.com",
        },
    };

    private sealed class FakeMicrosoftFlow : IMicrosoftPkceAuthorizationFlow
    {
        public async Task<OAuthTokenSet> ConnectAsync(
            string tenant,
            string clientId,
            IReadOnlyList<string> scopes,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var tokens = Tokens();
            await Task.CompletedTask;
            return tokens;
        }
    }

    private sealed class FakeGoogleFlow(OAuthTokenStore store) : IGooglePkceAuthorizationFlow
    {
        public async Task<OAuthTokenSet> ConnectAsync(
            string clientId,
            string tokenStoreKey,
            IReadOnlyList<string> scopes,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var tokens = Tokens();
            await store.SaveAsync(tokenStoreKey, tokens, cancellationToken);
            return tokens;
        }
    }

    private sealed class FakeIdentityResolver(string address) : IProviderEmailAccountIdentityResolver
    {
        public Task<EmailAccountIdentity> ResolveGoogleAsync(OAuthAccessToken token, CancellationToken cancellationToken) =>
            Task.FromResult(EmailAccountIdentity.Create(address));

        public Task<EmailAccountIdentity> ResolveMicrosoftAsync(OAuthAccessToken token, CancellationToken cancellationToken) =>
            Task.FromResult(EmailAccountIdentity.Create(address));
    }

    private sealed class NoOpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private static OAuthTokenSet Tokens() => new(
        "access-token",
        "refresh-token",
        DateTimeOffset.UtcNow.AddHours(1),
        "Bearer",
        "scope");

    private sealed class MemorySettingsStore(AppSettings settings) : ISettingsStore
    {
        private AppSettings _settings = settings;

        public bool FailSaves { get; init; }

        public Task<SettingsValidationResult> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(SettingsValidation.Normalize(_settings));

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailSaves)
            {
                throw new IOException("Simulated settings failure.");
            }

            _settings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);

        public Task SetAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_values.Remove(key, out var prior))
            {
                CryptographicOperations.ZeroMemory(prior);
            }

            _values[key] = secret.ToArray();
            return Task.CompletedTask;
        }

        public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_values.TryGetValue(key, out var value) ? value.ToArray() : null);
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_values.Remove(key, out var prior))
            {
                CryptographicOperations.ZeroMemory(prior);
            }

            return Task.CompletedTask;
        }

        public byte[]? Copy(string key) => _values.TryGetValue(key, out var value) ? value.ToArray() : null;
    }
}
