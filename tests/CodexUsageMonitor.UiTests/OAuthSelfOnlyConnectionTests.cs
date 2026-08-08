using System.Security.Cryptography;
using System.Net.Http;
using CodexUsageMonitor.App.Services;
using CodexUsageMonitor.Core.Security;
using CodexUsageMonitor.Core.Settings;
using CodexUsageMonitor.Email.Models;
using CodexUsageMonitor.Email.OAuth;
using CodexUsageMonitor.Persistence.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexUsageMonitor.UiTests;

[TestClass]
public sealed class OAuthSelfOnlyConnectionTests
{
    [TestMethod]
    public async Task GoogleConnectionDerivesIdentityAndDoesNotEnableSending()
    {
        var settingsStore = new MemorySettingsStore(new AppSettings());
        var settings = new ApplicationSettingsService(settingsStore);
        await settings.InitializeAsync(CancellationToken.None);
        var secrets = new MemorySecretStore();
        var tokens = new OAuthTokenStore(secrets);
        var service = new OAuthConnectionService(
            settings,
            secrets,
            tokens,
            new EmailProviderRegistrations("google-client", "microsoft-client", "common"),
            new FakeMicrosoftFlow(),
            new FakeGoogleFlow(tokens),
            new FakeIdentityResolver("person@example.com"),
            new HttpClient(new NoOpHandler()),
            NullLogger<OAuthConnectionService>.Instance);

        var status = await service.ConnectGoogleAsync(CancellationToken.None);

        Assert.AreEqual(OAuthConnectionState.Connected, status.State);
        Assert.AreEqual("person@example.com", status.ConnectedAddress);
        Assert.AreEqual(EmailProviderMode.Gmail, settings.Current.Email.Provider);
        Assert.AreEqual("person@example.com", settings.Current.Email.ConnectedAddress);
        Assert.IsFalse(settings.Current.Email.Enabled, "Connecting an account must not enable email notifications.");
        Assert.IsNull(settings.Current.Email.SenderAddress);
        Assert.AreEqual(0, settings.Current.Email.Recipients.Count);
        Assert.IsNotNull(settings.Current.Email.OAuthTokenReference);
    }

    [TestMethod]
    public async Task MissingProviderRegistrationFailsClosed()
    {
        var settingsStore = new MemorySettingsStore(new AppSettings());
        var settings = new ApplicationSettingsService(settingsStore);
        await settings.InitializeAsync(CancellationToken.None);
        var secrets = new MemorySecretStore();
        var tokens = new OAuthTokenStore(secrets);
        var service = new OAuthConnectionService(
            settings,
            secrets,
            tokens,
            new EmailProviderRegistrations(null, null, "common"),
            new FakeMicrosoftFlow(),
            new FakeGoogleFlow(tokens),
            new FakeIdentityResolver("person@example.com"),
            new HttpClient(new NoOpHandler()),
            NullLogger<OAuthConnectionService>.Instance);

        var status = await service.ConnectGoogleAsync(CancellationToken.None);

        Assert.AreEqual(OAuthConnectionState.Unavailable, status.State);
        Assert.IsNull(settings.Current.Email.OAuthTokenReference);
    }

    private sealed class FakeMicrosoftFlow : IMicrosoftPkceAuthorizationFlow
    {
        public Task<OAuthTokenSet> ConnectAsync(string tenant, string clientId, IReadOnlyList<string> scopes, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(TokenSet());
    }

    private sealed class FakeGoogleFlow(OAuthTokenStore store) : IGooglePkceAuthorizationFlow
    {
        public async Task<OAuthTokenSet> ConnectAsync(string clientId, string tokenStoreKey, IReadOnlyList<string> scopes, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var tokens = TokenSet();
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

    private static OAuthTokenSet TokenSet() => new(
        "access-token", "refresh-token", DateTimeOffset.UtcNow.AddHours(1), "Bearer", "scope");

    private sealed class NoOpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }

    private sealed class MemorySettingsStore(AppSettings settings) : ISettingsStore
    {
        private AppSettings _settings = settings;
        public Task<SettingsValidationResult> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(SettingsValidation.Normalize(_settings));
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
        {
            _settings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);
        public Task SetAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken)
        {
            _values[key] = secret.ToArray();
            return Task.CompletedTask;
        }
        public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(_values.TryGetValue(key, out var value) ? value.ToArray() : null);
        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            if (_values.Remove(key, out var value)) CryptographicOperations.ZeroMemory(value);
            return Task.CompletedTask;
        }
    }
}
