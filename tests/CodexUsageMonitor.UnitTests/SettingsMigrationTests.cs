using System.Text.Json;
using System.Text.Json.Nodes;
using CodexUsageMonitor.Core.Settings;
using CodexUsageMonitor.Persistence.Settings;
using Microsoft.Extensions.Logging.Abstractions;

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
        Assert.AreEqual(1, migration.SourceSchemaVersion);
        Assert.IsTrue(result.CanPersist);
        Assert.AreEqual(4, result.Settings.SchemaVersion);
        Assert.AreEqual(0.42, result.Settings.Widget.Opacity, 0.001);
        Assert.AreEqual(ResetTimeDisplayMode.Hidden, result.Settings.Widget.ResetTimeDisplay);
        Assert.IsFalse(result.Settings.Widget.Topmost);
        Assert.AreEqual("sender@example.com", result.Settings.Email.SmtpUsername);
        Assert.AreEqual(EmailProviderMode.OtherSmtp, result.Settings.Email.Provider);
        Assert.IsFalse(result.Settings.Email.Enabled);
        Assert.AreEqual(0, result.Settings.Email.Recipients.Count);
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
    [DataRow("{\"automaticChecks\":true}")]
    [DataRow("{\"automaticChecks\":true,\"manifestUri\":null}")]
    public void MigratesSchemaThreeMissingOrNullManifestUriToOfficialDefault(string updatesJson)
    {
        using var document = JsonDocument.Parse($$"""
        {
          "schemaVersion": 3,
          "updates": {{updatesJson}}
        }
        """);

        var migration = SettingsMigrator.ReadAndMigrate(document.RootElement);

        Assert.IsTrue(migration.Migrated);
        Assert.AreEqual(3, migration.SourceSchemaVersion);
        Assert.AreEqual(4, migration.Settings?.SchemaVersion);
        Assert.AreEqual(
            "https://github.com/saroo98/codex-usage-monitor/releases/latest/download/update-manifest.json",
            migration.Settings?.Updates.ManifestUri?.AbsoluteUri);
    }

    [TestMethod]
    public void MigratesSchemaThreeWithoutReplacingValidCustomManifestUri()
    {
        const string json = """
        {
          "schemaVersion": 3,
          "updates": {
            "automaticChecks": true,
            "manifestUri": "https://updates.example.test/custom/manifest.json?channel=stable"
          }
        }
        """;
        using var document = JsonDocument.Parse(json);

        var migration = SettingsMigrator.ReadAndMigrate(document.RootElement);
        var result = SettingsValidation.Normalize(new AppSettings { Updates = migration.Settings!.Updates });

        Assert.IsTrue(migration.Migrated);
        Assert.AreEqual(
            "https://updates.example.test/custom/manifest.json?channel=stable",
            result.Settings.Updates.ManifestUri?.AbsoluteUri);
        Assert.IsFalse(result.Issues.Any(issue => issue.Path == "updates.manifestUri"));
    }

    [TestMethod]
    [DataRow("http://updates.example.test/manifest.json")]
    [DataRow("https://user:password@updates.example.test/manifest.json")]
    [DataRow("https://updates.example.test/manifest.json#release")]
    [DataRow("not-a-valid-absolute-uri")]
    public void InvalidManifestUriDefaultsWithOnlyANonSensitiveIssueCode(string configuredValue)
    {
        var settings = new AppSettings
        {
            Updates = new UpdateSettings
            {
                ManifestUri = new Uri(configuredValue, UriKind.RelativeOrAbsolute),
            },
        };

        var result = SettingsValidation.Normalize(settings);

        Assert.AreEqual(
            "https://github.com/saroo98/codex-usage-monitor/releases/latest/download/update-manifest.json",
            result.Settings.Updates.ManifestUri?.AbsoluteUri);
        CollectionAssert.Contains(
            result.Issues.ToArray(),
            new SettingsValidationIssue("updates.manifestUri", "settings.update_manifest_defaulted"));
        Assert.IsFalse(result.Issues.Any(issue => issue.Code.Contains(configuredValue, StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow(3, "\"http://[::1\"")]
    [DataRow(4, "\"https://exa mple.test/x\"")]
    [DataRow(4, "42")]
    [DataRow(4, "true")]
    [DataRow(4, "{\"unexpected\":\"value\"}")]
    [DataRow(4, "[\"unexpected\"]")]
    public async Task PersistedInvalidManifestValueLoadsSafelyWithoutDiscardingOtherSettings(
        int sourceSchemaVersion,
        string persistedValue)
    {
        var directory = Path.Combine(Path.GetTempPath(), "cum-settings-uri", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settingsPath = Path.Combine(directory, "settings.json");
        try
        {
            var persisted = JsonSerializer.SerializeToNode(new AppSettings
            {
                General = new GeneralSettings { CloseToTray = false, Language = "cy" },
                History = new HistorySettings { RetentionDays = 41 },
                Updates = new UpdateSettings { AutomaticChecks = false },
            }, SettingsJson.TypeInfo)!.AsObject();
            persisted["schemaVersion"] = sourceSchemaVersion;
            var wrapper = JsonNode.Parse($$"""{"value":{{persistedValue}}}""")!.AsObject();
            persisted["updates"]!.AsObject()["manifestUri"] = wrapper["value"]?.DeepClone();
            await File.WriteAllTextAsync(settingsPath, persisted.ToJsonString(SettingsJson.Options));

            var store = new JsonSettingsStore(settingsPath, NullLogger<JsonSettingsStore>.Instance);
            var result = await store.LoadAsync(CancellationToken.None);

            Assert.IsTrue(result.CanPersist);
            Assert.AreEqual(sourceSchemaVersion, result.SourceSchemaVersion);
            Assert.AreEqual(4, result.Settings.SchemaVersion);
            Assert.IsFalse(result.Settings.General.CloseToTray);
            Assert.AreEqual("cy", result.Settings.General.Language);
            Assert.AreEqual(41, result.Settings.History.RetentionDays);
            Assert.IsFalse(result.Settings.Updates.AutomaticChecks);
            Assert.AreEqual(
                "https://github.com/saroo98/codex-usage-monitor/releases/latest/download/update-manifest.json",
                result.Settings.Updates.ManifestUri?.AbsoluteUri);
            CollectionAssert.AreEqual(
                new[] { new SettingsValidationIssue("updates.manifestUri", "settings.update_manifest_defaulted") },
                result.Issues.ToArray());
            Assert.IsFalse(result.Issues.Any(issue => issue.Code.Contains(persistedValue, StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void DisablingAutomaticChecksKeepsTheOfficialManifestConfigured()
    {
        const string json = """
        {
          "schemaVersion": 3,
          "updates": {
            "automaticChecks": false,
            "manifestUri": null
          }
        }
        """;
        using var document = JsonDocument.Parse(json);

        var migration = SettingsMigrator.ReadAndMigrate(document.RootElement);

        Assert.IsFalse(migration.Settings?.Updates.AutomaticChecks);
        Assert.AreEqual(
            "https://github.com/saroo98/codex-usage-monitor/releases/latest/download/update-manifest.json",
            migration.Settings?.Updates.ManifestUri?.AbsoluteUri);
    }

    [TestMethod]
    public void RemovesLegacyRecipientsAndPreservesOpacityFloor()
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
        Assert.AreEqual(0, result.Settings.Email.Recipients.Count);
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
