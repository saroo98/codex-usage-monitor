using System.Text.Json;
using CodexUsageMonitor.Core.Settings;

namespace CodexUsageMonitor.UnitTests;

[TestClass]
public sealed class SettingsMigrationTests
{
    [TestMethod]
    public void MigratesSchemaOneWithoutLosingExistingValues()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "general": { "startWithWindows": true, "privacyMode": false, "language": "en" },
          "widget": { "opacity": 0.42, "showResetTime": false, "topmost": false },
          "notifications": { "thresholds": [20, 10, 5, 0], "playSound": false },
          "email": {
            "provider": "GenericSmtp",
            "senderAddress": "sender@example.com",
            "recipientAddress": "receiver@example.com",
            "smtpHost": "smtp.example.com",
            "smtpPort": 587,
            "useTls": true,
            "credentialReference": "secret-ref"
          },
          "history": { "enabled": true, "retentionDays": 30, "sampleIntervalMinutes": 5 },
          "updates": { "automaticChecks": true, "channel": "Stable", "checkIntervalHours": 24 },
          "profiles": [{ "id": "9f220e47-b657-4c75-a535-235626e2a90c", "name": "Personal", "enabled": true, "monitorInBackground": true }]
        }
        """;
        using var document = JsonDocument.Parse(json);

        var migration = SettingsMigrator.ReadAndMigrate(document.RootElement);
        var result = SettingsValidation.Normalize(migration.Settings, migration.CanPersist, migration.SourceSchemaVersion);

        Assert.IsTrue(migration.Migrated);
        Assert.IsTrue(result.CanPersist);
        Assert.AreEqual(2, result.Settings.SchemaVersion);
        Assert.AreEqual(0.42, result.Settings.Widget.Opacity, 0.001);
        Assert.AreEqual(ResetTimeDisplayMode.Hidden, result.Settings.Widget.ResetTimeDisplay);
        Assert.IsFalse(result.Settings.Widget.Topmost);
        Assert.AreEqual("sender@example.com", result.Settings.Email.SmtpUsername);
        CollectionAssert.AreEqual(new[] { "receiver@example.com" }, result.Settings.Email.Recipients.ToArray());
        Assert.AreEqual(SmtpSecurityMode.StartTls, result.Settings.Email.SmtpSecurity);
        Assert.AreEqual("secret-ref", result.Settings.Email.CredentialReference);
        Assert.AreEqual(30, result.Settings.History.RetentionDays);
    }

    [TestMethod]
    public void RejectsFutureSchemaWithoutMakingItPersistable()
    {
        using var document = JsonDocument.Parse("{\"schemaVersion\":99}");
        var migration = SettingsMigrator.ReadAndMigrate(document.RootElement);

        Assert.IsNull(migration.Settings);
        Assert.IsFalse(migration.CanPersist);
        Assert.AreEqual("settings.future_schema", migration.SafeErrorCode);
    }

    [TestMethod]
    public void NormalizesRecipientsAndPreservesOpacityFloor()
    {
        var settings = new AppSettings
        {
            Widget = new WidgetSettings { Opacity = 0.35 },
            Email = new EmailSettings
            {
                Provider = EmailProviderMode.GenericSmtp,
                SenderAddress = "sender@example.com",
                Recipients = ["A@example.com", "a@example.com", "b@example.com"],
                SmtpHost = "smtp.example.com",
            },
        };

        var result = SettingsValidation.Normalize(settings);

        Assert.AreEqual(0.35, result.Settings.Widget.Opacity, 0.001);
        CollectionAssert.AreEqual(new[] { "A@example.com", "b@example.com" }, result.Settings.Email.Recipients.ToArray());
    }

    [TestMethod]
    public void SerializedSettingsContainNoSecretMaterialFields()
    {
        var json = JsonSerializer.Serialize(new AppSettings
        {
            Email = new EmailSettings
            {
                CredentialReference = "opaque-reference",
                OAuthTokenReference = "opaque-token-reference",
            },
        }, SettingsJson.TypeInfo);

        Assert.IsFalse(json.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("refreshToken", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("clientSecret", StringComparison.OrdinalIgnoreCase));
    }
}
