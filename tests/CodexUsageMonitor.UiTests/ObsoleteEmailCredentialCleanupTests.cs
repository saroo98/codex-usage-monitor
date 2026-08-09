using CodexUsageMonitor.App.Services;
using CodexUsageMonitor.Core.Security;
using CodexUsageMonitor.Core.Settings;
using CodexUsageMonitor.Persistence.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexUsageMonitor.UiTests;

[TestClass]
public sealed class ObsoleteEmailCredentialCleanupTests
{
    [TestMethod]
    public async Task StartupCleanupDeletesMigratedReferencesAndClearsSettings()
    {
        var store = new MemorySettingsStore(new AppSettings
        {
            Email = new EmailSettings { ObsoleteSecretReferences = ["legacy-oauth", "legacy-smtp"] },
        });
        var settings = new ApplicationSettingsService(store);
        await settings.InitializeAsync(CancellationToken.None);
        var secrets = new MemorySecretStore();
        await secrets.SetAsync("legacy-oauth", new byte[] { 1 }, CancellationToken.None);
        await secrets.SetAsync("legacy-smtp", new byte[] { 2 }, CancellationToken.None);
        var cleanup = new ObsoleteEmailCredentialCleanup(
            settings,
            secrets,
            NullLogger<ObsoleteEmailCredentialCleanup>.Instance);

        await cleanup.RunAsync(CancellationToken.None);

        Assert.IsNull(await secrets.GetAsync("legacy-oauth", CancellationToken.None));
        Assert.IsNull(await secrets.GetAsync("legacy-smtp", CancellationToken.None));
        Assert.AreEqual(0, settings.Current.Email.ObsoleteSecretReferences.Count);
    }

    [TestMethod]
    public async Task FailedDeletionRemainsQueuedWithoutExposingSecretMaterial()
    {
        var store = new MemorySettingsStore(new AppSettings
        {
            Email = new EmailSettings { ObsoleteSecretReferences = ["retry-reference"] },
        });
        var settings = new ApplicationSettingsService(store);
        await settings.InitializeAsync(CancellationToken.None);
        var cleanup = new ObsoleteEmailCredentialCleanup(
            settings,
            new FailingSecretStore(),
            NullLogger<ObsoleteEmailCredentialCleanup>.Instance);

        await cleanup.RunAsync(CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "retry-reference" }, settings.Current.Email.ObsoleteSecretReferences.ToArray());
    }

    private sealed class MemorySettingsStore(AppSettings value) : ISettingsStore
    {
        private AppSettings _value = value;
        public Task<SettingsValidationResult> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(SettingsValidation.Normalize(_value));
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
        {
            _value = settings;
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
            _values.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class FailingSecretStore : ISecretStore
    {
        public Task SetAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);
        public Task DeleteAsync(string key, CancellationToken cancellationToken) => throw new IOException("simulated");
    }
}
