using System.Security;
using System.Security.Cryptography;
using System.Text;
using CodexUsageMonitor.App.Services;
using CodexUsageMonitor.Core.Security;
using CodexUsageMonitor.Core.Settings;
using CodexUsageMonitor.Email.Security;
using CodexUsageMonitor.Persistence.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexUsageMonitor.UiTests;

[TestClass]
public sealed class EmailCredentialServiceTests
{
    [TestMethod]
    public async Task StorePersistsOnlyCredentialReferenceAndSecret()
    {
        var initial = CreateSettings();
        var settingsStore = new MemorySettingsStore(initial);
        var settings = new ApplicationSettingsService(settingsStore);
        await settings.InitializeAsync(CancellationToken.None);
        var secrets = new MemorySecretStore();
        var service = new EmailCredentialService(settings, secrets, NullLogger<EmailCredentialService>.Instance);

        using var password = Secure("correct horse battery staple");
        var status = await service.StoreSmtpPasswordAsync("sender@example.com", password, CancellationToken.None);

        var reference = EmailSecretKeyFactory.SmtpPassword("sender@example.com");
        Assert.AreEqual(EmailCredentialState.Stored, status.State);
        Assert.AreEqual(reference, settings.Current.Email.CredentialReference);
        Assert.AreEqual(EmailProviderMode.GenericSmtp, settings.Current.Email.Provider);
        Assert.AreEqual("correct horse battery staple", Encoding.UTF8.GetString(secrets.Copy(reference)!));
        Assert.IsFalse(settingsStore.LastSavedJsonLikeText.Contains("correct horse", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task StoreRestoresPriorSecretWhenSettingsPersistenceFails()
    {
        var initial = CreateSettings() with
        {
            Email = CreateSettings().Email with
            {
                CredentialReference = EmailSecretKeyFactory.SmtpPassword("sender@example.com"),
            },
        };
        var settingsStore = new MemorySettingsStore(initial) { FailSaves = true };
        var settings = new ApplicationSettingsService(settingsStore);
        await settings.InitializeAsync(CancellationToken.None);
        var secrets = new MemorySecretStore();
        var reference = initial.Email.CredentialReference!;
        await secrets.SetAsync(reference, Encoding.UTF8.GetBytes("old-password"), CancellationToken.None);
        var service = new EmailCredentialService(settings, secrets, NullLogger<EmailCredentialService>.Instance);

        using var replacement = Secure("new-password");
        await Assert.ThrowsAsync<IOException>(() =>
            service.StoreSmtpPasswordAsync("sender@example.com", replacement, CancellationToken.None));

        Assert.AreEqual("old-password", Encoding.UTF8.GetString(secrets.Copy(reference)!));
        Assert.AreEqual(reference, settings.Current.Email.CredentialReference);
    }

    [TestMethod]
    public async Task RemoveClearsReferenceAndDeletesSecret()
    {
        var reference = EmailSecretKeyFactory.SmtpPassword("sender@example.com");
        var initial = CreateSettings() with
        {
            Email = CreateSettings().Email with { CredentialReference = reference },
        };
        var settingsStore = new MemorySettingsStore(initial);
        var settings = new ApplicationSettingsService(settingsStore);
        await settings.InitializeAsync(CancellationToken.None);
        var secrets = new MemorySecretStore();
        await secrets.SetAsync(reference, Encoding.UTF8.GetBytes("password"), CancellationToken.None);
        var service = new EmailCredentialService(settings, secrets, NullLogger<EmailCredentialService>.Instance);

        var status = await service.RemoveSmtpPasswordAsync(CancellationToken.None);

        Assert.AreEqual(EmailCredentialState.NotStored, status.State);
        Assert.IsNull(settings.Current.Email.CredentialReference);
        Assert.IsNull(secrets.Copy(reference));
    }

    [TestMethod]
    public async Task RemoveRestoresSecretWhenSettingsPersistenceFails()
    {
        var reference = EmailSecretKeyFactory.SmtpPassword("sender@example.com");
        var initial = CreateSettings() with
        {
            Email = CreateSettings().Email with { CredentialReference = reference },
        };
        var settingsStore = new MemorySettingsStore(initial) { FailSaves = true };
        var settings = new ApplicationSettingsService(settingsStore);
        await settings.InitializeAsync(CancellationToken.None);
        var secrets = new MemorySecretStore();
        await secrets.SetAsync(reference, Encoding.UTF8.GetBytes("password"), CancellationToken.None);
        var service = new EmailCredentialService(settings, secrets, NullLogger<EmailCredentialService>.Instance);

        await Assert.ThrowsAsync<IOException>(() => service.RemoveSmtpPasswordAsync(CancellationToken.None));

        Assert.AreEqual("password", Encoding.UTF8.GetString(secrets.Copy(reference)!));
        Assert.AreEqual(reference, settings.Current.Email.CredentialReference);
    }

    private static AppSettings CreateSettings() => new()
    {
        Email = new EmailSettings
        {
            Provider = EmailProviderMode.GenericSmtp,
            SenderAddress = "sender@example.com",
            Recipients = ["recipient@example.com"],
            SmtpHost = "smtp.example.com",
        },
    };

    private static SecureString Secure(string value)
    {
        var secret = new SecureString();
        foreach (var character in value)
        {
            secret.AppendChar(character);
        }

        secret.MakeReadOnly();
        return secret;
    }

    private sealed class MemorySettingsStore(AppSettings settings) : ISettingsStore
    {
        private AppSettings _settings = settings;

        public bool FailSaves { get; init; }

        public string LastSavedJsonLikeText { get; private set; } = string.Empty;

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
            LastSavedJsonLikeText = settings.ToString() ?? string.Empty;
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
