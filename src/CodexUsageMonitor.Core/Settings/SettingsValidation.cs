using System.Net.Mail;

namespace CodexUsageMonitor.Core.Settings;

public sealed record SettingsValidationIssue(string Path, string Code);

public sealed record SettingsValidationResult(
    AppSettings Settings,
    IReadOnlyList<SettingsValidationIssue> Issues,
    bool CanPersist = true,
    int SourceSchemaVersion = AppSettings.CurrentSchemaVersion)
{
    public bool IsValid => Issues.Count == 0;
}

public static class SettingsValidation
{
    public static SettingsValidationResult Normalize(
        AppSettings? input,
        bool canPersist = true,
        int sourceSchemaVersion = AppSettings.CurrentSchemaVersion)
    {
        var source = input ?? new AppSettings();
        var issues = new List<SettingsValidationIssue>();
        if (source.SchemaVersion is <= 0 or > AppSettings.CurrentSchemaVersion)
        {
            issues.Add(new("schemaVersion", "settings.unsupported_schema"));
            canPersist = false;
        }

        var opacity = Math.Clamp(source.Widget.Opacity, 0.35, 1.0);
        if (opacity != source.Widget.Opacity)
        {
            issues.Add(new("widget.opacity", "settings.opacity_clamped"));
        }

        var thresholds = source.Notifications.Thresholds
            .Where(static value => value is >= 0 and <= 100)
            .Distinct()
            .OrderDescending()
            .ToArray();
        if (thresholds.Length == 0)
        {
            thresholds = [20, 10, 5, 0];
            issues.Add(new("notifications.thresholds", "settings.thresholds_defaulted"));
        }

        var profiles = source.Profiles
            .Where(static profile => profile.Id != Guid.Empty && !string.IsNullOrWhiteSpace(profile.Name))
            .GroupBy(static profile => profile.Id)
            .Select(static group =>
            {
                var profile = group.First();
                var codexHome = profile.CodexHome;
                return profile with
                {
                    Name = profile.Name.Trim(),
                    CodexHome = string.IsNullOrWhiteSpace(codexHome) ? null : codexHome.Trim(),
                };
            })
            .Take(8)
            .ToArray();
        if (profiles.Length == 0)
        {
            profiles = [ProfileSettings.FromDefinition(CodexUsageMonitor.Core.Profiles.ProfileDefinition.CreateDefault())];
            issues.Add(new("profiles", "settings.profile_defaulted"));
        }

        ValidateEmail(source.Email, issues);
        var normalized = source with
        {
            SchemaVersion = AppSettings.CurrentSchemaVersion,
            General = source.General with
            {
                Language = string.IsNullOrWhiteSpace(source.General.Language) ? "en" : source.General.Language.Trim(),
            },
            Widget = source.Widget with
            {
                Opacity = opacity,
                GlobalHotkey = NormalizeHotkey(source.Widget.GlobalHotkey, issues),
            },
            Notifications = source.Notifications with { Thresholds = thresholds },
            History = source.History with
            {
                RetentionDays = Math.Clamp(source.History.RetentionDays, 7, 365),
                SampleIntervalMinutes = Math.Clamp(source.History.SampleIntervalMinutes, 1, 60),
            },
            Updates = source.Updates with
            {
                CheckIntervalHours = Math.Clamp(source.Updates.CheckIntervalHours, 4, 168),
            },
            Email = source.Email with
            {
                Enabled = source.Email.Provider is not EmailProviderMode.Off && source.Email.Enabled,
                ConnectedAddress = NormalizeOptional(source.Email.ConnectedAddress),
                SenderAddress = NormalizeOptional(source.Email.SenderAddress),
                Recipients = [],
                SmtpHost = NormalizeOptional(source.Email.SmtpHost),
                SmtpPort = Math.Clamp(source.Email.SmtpPort, 1, 65535),
                SmtpUsername = NormalizeOptional(source.Email.SmtpUsername),
                SmtpSecurity = source.Email.SmtpSecurity is SmtpSecurityMode.None
                    ? SmtpSecurityMode.StartTls
                    : source.Email.SmtpSecurity,
                CredentialReference = NormalizeReference(source.Email.CredentialReference),
                OAuthClientId = NormalizeOptional(source.Email.OAuthClientId),
                OAuthTenant = NormalizeOptional(source.Email.OAuthTenant) ?? "common",
                OAuthTokenReference = NormalizeReference(source.Email.OAuthTokenReference),
                OAuthRegistrationId = NormalizeReference(source.Email.OAuthRegistrationId),
                ObsoleteSecretReferences = source.Email.ObsoleteSecretReferences
                    .Select(NormalizeReference)
                    .Where(static value => value is not null)
                    .Cast<string>()
                    .Distinct(StringComparer.Ordinal)
                    .Take(8)
                    .ToArray(),
            },
            Profiles = profiles,
        };
        return new SettingsValidationResult(normalized, issues.AsReadOnly(), canPersist, sourceSchemaVersion);
    }

    private static void ValidateEmail(
        EmailSettings settings,
        List<SettingsValidationIssue> issues)
    {
        if (settings.Provider is EmailProviderMode.Off)
        {
            return;
        }

        if (settings.Provider is EmailProviderMode.Gmail or EmailProviderMode.Microsoft365)
        {
            if (!IsEmail(settings.ConnectedAddress))
            {
                issues.Add(new("email.connectedAddress", "settings.email_connection_required"));
            }

            if (string.IsNullOrWhiteSpace(settings.OAuthClientId) || string.IsNullOrWhiteSpace(settings.OAuthTokenReference))
            {
                issues.Add(new("email.oauth", "settings.oauth_connection_required"));
            }

            return;
        }

        if (!IsEmail(settings.SenderAddress))
        {
            issues.Add(new("email.senderAddress", "settings.invalid_email"));
        }

        if (string.IsNullOrWhiteSpace(settings.SmtpHost))
        {
            issues.Add(new("email.smtpHost", "settings.smtp_host_required"));
        }

        if (settings.SmtpSecurity is SmtpSecurityMode.None)
        {
            issues.Add(new("email.smtpSecurity", "settings.smtp_encryption_required"));
        }

        if (settings.Provider is EmailProviderMode.ProtonMailBridge)
        {
            if (settings.SmtpHost is not ("127.0.0.1" or "::1" or "localhost"))
            {
                issues.Add(new("email.smtpHost", "settings.proton_bridge_loopback_required"));
            }

            if (string.IsNullOrWhiteSpace(settings.SmtpUsername))
            {
                issues.Add(new("email.smtpUsername", "settings.proton_bridge_username_required"));
            }
        }
    }

    private static GlobalHotkeySettings NormalizeHotkey(
        GlobalHotkeySettings? hotkey,
        List<SettingsValidationIssue> issues)
    {
        var source = hotkey ?? new GlobalHotkeySettings();
        var key = string.IsNullOrWhiteSpace(source.Key) ? "U" : source.Key.Trim().ToUpperInvariant();
        if (key.Length is < 1 or > 16)
        {
            key = "U";
            issues.Add(new("widget.globalHotkey.key", "settings.hotkey_defaulted"));
        }

        var modifiers = string.IsNullOrWhiteSpace(source.Modifiers)
            ? "Control+Shift"
            : source.Modifiers.Trim();
        if (modifiers.Length > 64)
        {
            modifiers = "Control+Shift";
            issues.Add(new("widget.globalHotkey.modifiers", "settings.hotkey_defaulted"));
        }

        return source with { Key = key, Modifiers = modifiers };
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeReference(string? value)
    {
        var normalized = NormalizeOptional(value);
        return normalized is { Length: <= 256 } ? normalized : null;
    }

    private static bool IsEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 320)
        {
            return false;
        }

        try
        {
            return new MailAddress(value).Address == value.Trim();
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
