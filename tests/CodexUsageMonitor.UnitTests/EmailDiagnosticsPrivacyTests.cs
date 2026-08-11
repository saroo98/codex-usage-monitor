using System.Reflection;
using System.Text.Json;
using CodexUsageMonitor.Core.Settings;
using CodexUsageMonitor.Persistence.Diagnostics;

namespace CodexUsageMonitor.UnitTests;

[TestClass]
public sealed class EmailDiagnosticsPrivacyTests
{
    [TestMethod]
    public void RedactedSupportSettingsContainNoEmailIdentityOrCredentialReference()
    {
        var method = typeof(SupportBundleBuilder).GetMethod(
            "RedactedSettings",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new AssertFailedException("Redacted settings boundary is missing.");
        var settings = new AppSettings
        {
            Email = new EmailSettings
            {
                Provider = EmailProviderMode.Gmail,
                Enabled = true,
                ConnectedAddress = "private@example.com",
                SenderAddress = "legacy@example.com",
                Recipients = ["other@example.com"],
                CredentialReference = "credential-secret-reference",
                OAuthTokenReference = "oauth-secret-reference",
                OAuthClientId = "public-client-registration",
            },
        };

        var redacted = method.Invoke(null, [settings]);
        var json = JsonSerializer.Serialize(redacted);

        Assert.IsFalse(json.Contains("private@example.com", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("legacy@example.com", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("other@example.com", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("credential-secret-reference", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("oauth-secret-reference", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("public-client-registration", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RedactedSupportSettingsProjectUpdateConfigurationWithoutManifestUri()
    {
        var method = typeof(SupportBundleBuilder).GetMethod(
            "RedactedSettings",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new AssertFailedException("Redacted settings boundary is missing.");
        const string privateHost = "task5-private-feed-4f39.example.test";
        const string privatePath = "customer-92831/private-update-manifest.json";
        var settings = new AppSettings
        {
            Updates = new UpdateSettings
            {
                AutomaticChecks = false,
                AutomaticDownload = true,
                InstallOnExit = true,
                Channel = UpdateChannel.Preview,
                CheckIntervalHours = 36,
                ManifestUri = new Uri($"https://{privateHost}/{privatePath}?tenant=private-tenant-773"),
                LastCheckAtUtc = new DateTimeOffset(2026, 8, 10, 18, 30, 0, TimeSpan.Zero),
                ManifestEntityTag = "safe-etag",
                LastOfferedVersion = "6.1.0",
            },
        };

        var redacted = method.Invoke(null, [settings]);
        var json = JsonSerializer.Serialize(redacted, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        var updates = document.RootElement.GetProperty("updates");

        Assert.IsFalse(updates.GetProperty("automaticChecks").GetBoolean());
        Assert.IsTrue(updates.GetProperty("automaticDownload").GetBoolean());
        Assert.IsTrue(updates.GetProperty("installOnExit").GetBoolean());
        Assert.AreEqual("Preview", updates.GetProperty("channel").GetString());
        Assert.AreEqual(36, updates.GetProperty("checkIntervalHours").GetInt32());
        Assert.IsTrue(updates.GetProperty("manifestConfigured").GetBoolean());
        Assert.AreEqual("safe-etag", updates.GetProperty("manifestEntityTag").GetString());
        Assert.AreEqual("6.1.0", updates.GetProperty("lastOfferedVersion").GetString());
        Assert.IsFalse(updates.TryGetProperty("manifestUri", out _));
        Assert.IsFalse(json.Contains(privateHost, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains(privatePath, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("private-tenant-773", StringComparison.OrdinalIgnoreCase));
    }
}
