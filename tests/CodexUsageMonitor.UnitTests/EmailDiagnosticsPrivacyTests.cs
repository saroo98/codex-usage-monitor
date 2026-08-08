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
}
