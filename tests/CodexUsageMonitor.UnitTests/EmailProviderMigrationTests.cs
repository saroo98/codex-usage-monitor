using System.Text.Json;
using CodexUsageMonitor.Core.Settings;

namespace CodexUsageMonitor.UnitTests;

[TestClass]
public sealed class EmailProviderMigrationTests
{
    [TestMethod]
    [DataRow("GoogleOAuth", EmailProviderMode.Gmail)]
    [DataRow("MicrosoftOAuth", EmailProviderMode.Microsoft365)]
    [DataRow("GenericSmtp", EmailProviderMode.OtherSmtp)]
    [DataRow("Disabled", EmailProviderMode.Off)]
    public void VersionTwoProvidersMigrateWithoutKeepingBroadOAuthAuthorization(
        string legacyProvider,
        EmailProviderMode expectedProvider)
    {
        using var document = JsonDocument.Parse($$"""
            {
              "schemaVersion": 2,
              "email": {
                "provider": "{{legacyProvider}}",
                "senderAddress": "person@example.com",
                "recipients": ["other@example.com"],
                "oauthClientId": "legacy-client",
                "oauthTokenReference": "legacy-token-reference",
                "credentialReference": "smtp-reference"
              }
            }
            """);

        var result = SettingsMigrator.ReadAndMigrate(document.RootElement);

        Assert.IsNotNull(result.Settings);
        Assert.AreEqual(AppSettings.CurrentSchemaVersion, result.Settings.SchemaVersion);
        Assert.AreEqual(expectedProvider, result.Settings.Email.Provider);
        Assert.IsFalse(result.Settings.Email.Enabled);
        Assert.IsNull(result.Settings.Email.ConnectedAddress);
        Assert.AreEqual(0, result.Settings.Email.Recipients.Count);
        if (expectedProvider is EmailProviderMode.Gmail or EmailProviderMode.Microsoft365)
        {
            Assert.IsNull(result.Settings.Email.OAuthTokenReference);
            Assert.IsNull(result.Settings.Email.CredentialReference);
            CollectionAssert.Contains(result.Settings.Email.ObsoleteSecretReferences.ToArray(), "legacy-token-reference");
            CollectionAssert.Contains(result.Settings.Email.ObsoleteSecretReferences.ToArray(), "smtp-reference");
        }
    }
}
