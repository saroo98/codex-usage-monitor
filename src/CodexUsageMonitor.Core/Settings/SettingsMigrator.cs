using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexUsageMonitor.Core.Settings;

public sealed record SettingsMigrationResult(
    AppSettings? Settings,
    int SourceSchemaVersion,
    bool Migrated,
    bool CanPersist,
    string? SafeErrorCode);

public static class SettingsMigrator
{
    private const string InvalidManifestUriSentinel = "invalid-manifest-uri";

    public static SettingsMigrationResult ReadAndMigrate(JsonElement root)
    {
        if (root.ValueKind is not JsonValueKind.Object)
        {
            return new(null, 0, false, false, "settings.root_not_object");
        }

        var sourceVersion = root.TryGetProperty("schemaVersion", out var schemaElement) && schemaElement.TryGetInt32(out var parsed)
            ? parsed
            : 1;
        if (sourceVersion <= 0)
        {
            return new(null, sourceVersion, false, false, "settings.invalid_schema");
        }

        if (sourceVersion > AppSettings.CurrentSchemaVersion)
        {
            return new(null, sourceVersion, false, false, "settings.future_schema");
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(root.GetRawText(), new JsonNodeOptions { PropertyNameCaseInsensitive = false });
        }
        catch (JsonException)
        {
            return new(null, sourceVersion, false, false, "settings.invalid_json");
        }

        if (node is not JsonObject document)
        {
            return new(null, sourceVersion, false, false, "settings.root_not_object");
        }

        var migrated = false;
        var currentVersion = sourceVersion;
        if (currentVersion == 1)
        {
            MigrateVersion1To2(document);
            migrated = true;
            currentVersion = 2;
        }

        if (currentVersion == 2)
        {
            MigrateVersion2To3(document);
            migrated = true;
            currentVersion = 3;
        }

        if (currentVersion == 3)
        {
            MigrateVersion3To4(document);
            migrated = true;
        }

        document["schemaVersion"] = AppSettings.CurrentSchemaVersion;
        SanitizeManifestUriForDeserialization(document);
        try
        {
            var settings = document.Deserialize(SettingsJson.TypeInfo);
            return new(settings, sourceVersion, migrated, true, null);
        }
        catch (JsonException)
        {
            return new(null, sourceVersion, migrated, false, "settings.deserialize_failed");
        }
    }

    private static void MigrateVersion1To2(JsonObject document)
    {
        if (document["widget"] is JsonObject widget)
        {
            var showResetTime = widget["showResetTime"]?.GetValue<bool?>() ?? true;
            widget["resetTimeDisplay"] = showResetTime ? nameof(ResetTimeDisplayMode.Countdown) : nameof(ResetTimeDisplayMode.Hidden);
            widget.Remove("showResetTime");
            widget["globalHotkey"] ??= new JsonObject
            {
                ["enabled"] = true,
                ["modifiers"] = "Control+Shift",
                ["key"] = "U",
            };
        }

        if (document["email"] is JsonObject email)
        {
            var sender = email["senderAddress"]?.GetValue<string?>();
            var recipient = email["recipientAddress"]?.GetValue<string?>();
            var useTls = email["useTls"]?.GetValue<bool?>() ?? true;
            email["smtpUsername"] ??= sender;
            email["smtpSecurity"] = useTls ? nameof(SmtpSecurityMode.StartTls) : nameof(SmtpSecurityMode.None);
            email["recipients"] = string.IsNullOrWhiteSpace(recipient)
                ? new JsonArray()
                : new JsonArray(recipient.Trim());
            email["events"] ??= JsonSerializer.SerializeToNode(new EmailEventPreferences(), SettingsJson.Options);
            email.Remove("recipientAddress");
            email.Remove("useTls");
        }

        if (document["notifications"] is JsonObject notifications)
        {
            notifications["notifyOnConnectionRestored"] ??= false;
            notifications["notifyOnResetCredit"] ??= true;
            notifications["criticalBypassesQuietHours"] ??= false;
        }

        if (document["updates"] is JsonObject updates)
        {
            updates["automaticDownload"] ??= false;
            updates["installOnExit"] ??= false;
        }
    }

    private static void MigrateVersion2To3(JsonObject document)
    {
        if (document["email"] is not JsonObject email)
        {
            return;
        }

        var legacyProvider = email["provider"]?.GetValue<string?>();
        var migratedProvider = legacyProvider switch
        {
            "GoogleOAuth" => nameof(EmailProviderMode.Gmail),
            "MicrosoftOAuth" => nameof(EmailProviderMode.Microsoft365),
            "GenericSmtp" => nameof(EmailProviderMode.OtherSmtp),
            "Disabled" => nameof(EmailProviderMode.Off),
            _ => nameof(EmailProviderMode.Off),
        };
        var obsoleteReferences = new JsonArray();
        if (legacyProvider is "GoogleOAuth" or "MicrosoftOAuth")
        {
            if (email["oauthTokenReference"]?.GetValue<string?>() is { Length: > 0 } tokenReference)
            {
                obsoleteReferences.Add(tokenReference);
            }

            if (email["credentialReference"]?.GetValue<string?>() is { Length: > 0 } credentialReference)
            {
                obsoleteReferences.Add(credentialReference);
            }

            email["oauthTokenReference"] = null;
            email["oauthRegistrationId"] = null;
            email["oauthClientId"] = null;
            email["credentialReference"] = null;
        }

        email["provider"] = migratedProvider;
        email["enabled"] = false;
        email["connectedAddress"] = null;
        email["recipients"] = new JsonArray();
        email["obsoleteSecretReferences"] = obsoleteReferences;
    }

    private static void MigrateVersion3To4(JsonObject document)
    {
        if (!document.TryGetPropertyValue("updates", out var updatesNode) || updatesNode is null)
        {
            document["updates"] = new JsonObject
            {
                ["manifestUri"] = UpdateSettings.DefaultManifestUri.AbsoluteUri,
            };
            return;
        }

        if (updatesNode is JsonObject updates &&
            (!updates.TryGetPropertyValue("manifestUri", out var manifestUriNode) || manifestUriNode is null))
        {
            updates["manifestUri"] = UpdateSettings.DefaultManifestUri.AbsoluteUri;
        }
    }

    private static void SanitizeManifestUriForDeserialization(JsonObject document)
    {
        if (document["updates"] is not JsonObject updates ||
            !updates.TryGetPropertyValue("manifestUri", out var manifestUriNode) ||
            manifestUriNode is null)
        {
            return;
        }

        if (manifestUriNode is not JsonValue value ||
            !value.TryGetValue<string>(out var manifestUriText) ||
            !Uri.TryCreate(manifestUriText, UriKind.RelativeOrAbsolute, out _))
        {
            updates["manifestUri"] = InvalidManifestUriSentinel;
        }
    }
}
