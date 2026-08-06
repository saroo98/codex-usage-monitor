using System.Text.Json.Serialization;
using CodexUsageMonitor.Core.Profiles;
using CodexUsageMonitor.Core.Usage;

namespace CodexUsageMonitor.Core.Settings;

public enum WidgetSize
{
    Medium,
    Small,
    ExtraSmall,
}

public enum AppTheme
{
    System,
    Light,
    Dark,
    HighContrast,
}

public enum ResetTimeDisplayMode
{
    Hidden,
    Countdown,
    Exact,
    Both,
}

public enum UpdateChannel
{
    Stable,
    Preview,
}

public enum EmailProviderMode
{
    Disabled,
    GenericSmtp,
    MicrosoftOAuth,
    GoogleOAuth,
}

public enum SmtpSecurityMode
{
    Auto,
    StartTls,
    Tls,
    None,
}

public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public GeneralSettings General { get; init; } = new();

    public WidgetSettings Widget { get; init; } = new();

    public LimitSettings Limits { get; init; } = new();

    public NotificationSettings Notifications { get; init; } = new();

    public EmailSettings Email { get; init; } = new();

    public HistorySettings History { get; init; } = new();

    public UpdateSettings Updates { get; init; } = new();

    public IReadOnlyList<ProfileSettings> Profiles { get; init; } = [ProfileSettings.FromDefinition(ProfileDefinition.CreateDefault())];
}

public sealed record GeneralSettings
{
    public bool StartWithWindows { get; init; }

    public bool CloseToTray { get; init; } = true;

    public bool LaunchMinimized { get; init; }

    public bool ShowOnboardingOnNextLaunch { get; init; } = true;

    public bool PrivacyMode { get; init; } = true;

    public string Language { get; init; } = "en";
}

public sealed record WidgetSettings
{
    [JsonConverter(typeof(JsonStringEnumConverter<WidgetSize>))]
    public WidgetSize Size { get; init; } = WidgetSize.Medium;

    [JsonConverter(typeof(JsonStringEnumConverter<AppTheme>))]
    public AppTheme Theme { get; init; } = AppTheme.System;

    public double Opacity { get; init; } = 0.94;

    public bool Topmost { get; init; } = true;

    public bool Locked { get; init; }

    public bool ClickThrough { get; init; }

    public bool SnapToEdges { get; init; } = true;

    public bool AllowTaskbarOverlap { get; init; }

    public bool ReduceMotion { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter<ResetTimeDisplayMode>))]
    public ResetTimeDisplayMode ResetTimeDisplay { get; init; } = ResetTimeDisplayMode.Countdown;

    public bool ShowAccountLabel { get; init; } = true;

    public GlobalHotkeySettings GlobalHotkey { get; init; } = new();

    public WidgetPlacement? Placement { get; init; }
}

public sealed record GlobalHotkeySettings
{
    public bool Enabled { get; init; } = true;

    public string Modifiers { get; init; } = "Control+Shift";

    public string Key { get; init; } = "U";
}

public sealed record WidgetPlacement(
    string MonitorDeviceName,
    double LeftDip,
    double TopDip,
    double WidthDip,
    double HeightDip,
    double DpiScaleX,
    double DpiScaleY,
    DateTimeOffset SavedAtUtc);

public sealed record LimitSettings
{
    [JsonConverter(typeof(JsonStringEnumConverter<LimitSelectionMode>))]
    public LimitSelectionMode SelectionMode { get; init; } = LimitSelectionMode.AutoLowest;

    public string? ExplicitLimitIdentity { get; init; }

    public string? PreferredModel { get; init; }

    public bool MediumDualMeter { get; init; } = true;
}

public sealed record NotificationSettings
{
    public bool Enabled { get; init; } = true;

    public IReadOnlyList<int> Thresholds { get; init; } = [20, 10, 5, 0];

    public bool NotifyOnReset { get; init; } = true;

    public bool NotifyOnConnectionLoss { get; init; } = true;

    public bool NotifyOnConnectionRestored { get; init; }

    public bool NotifyOnResetCredit { get; init; } = true;

    public bool PlaySound { get; init; } = true;

    public bool QuietHoursEnabled { get; init; }

    public TimeOnly QuietHoursStart { get; init; } = new(22, 0);

    public TimeOnly QuietHoursEnd { get; init; } = new(8, 0);

    public bool CriticalBypassesQuietHours { get; init; }
}

public sealed record EmailEventPreferences
{
    public bool Thresholds { get; init; } = true;

    public bool Depleted { get; init; } = true;

    public bool Reset { get; init; } = true;

    public bool ConnectionLoss { get; init; }

    public bool ConnectionRestored { get; init; }

    public bool ResetCreditAvailable { get; init; }
}

public sealed record EmailSettings
{
    [JsonConverter(typeof(JsonStringEnumConverter<EmailProviderMode>))]
    public EmailProviderMode Provider { get; init; } = EmailProviderMode.Disabled;

    public string? SenderAddress { get; init; }

    public IReadOnlyList<string> Recipients { get; init; } = [];

    public string? SmtpHost { get; init; }

    public int SmtpPort { get; init; } = 587;

    public string? SmtpUsername { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter<SmtpSecurityMode>))]
    public SmtpSecurityMode SmtpSecurity { get; init; } = SmtpSecurityMode.StartTls;

    public string? CredentialReference { get; init; }

    public string? OAuthClientId { get; init; }

    public string? OAuthTenant { get; init; } = "common";

    public string? OAuthTokenReference { get; init; }

    public string? OAuthRegistrationId { get; init; }

    public bool IncludeAccountLabel { get; init; }

    public EmailEventPreferences Events { get; init; } = new();
}

public sealed record HistorySettings
{
    public bool Enabled { get; init; } = true;

    public int RetentionDays { get; init; } = 90;

    public int SampleIntervalMinutes { get; init; } = 5;
}

public sealed record UpdateSettings
{
    public bool AutomaticChecks { get; init; } = true;

    public bool AutomaticDownload { get; init; }

    public bool InstallOnExit { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter<UpdateChannel>))]
    public UpdateChannel Channel { get; init; } = UpdateChannel.Stable;

    public int CheckIntervalHours { get; init; } = 24;

    public Uri? ManifestUri { get; init; }

    public DateTimeOffset? LastCheckAtUtc { get; init; }

    public string? ManifestEntityTag { get; init; }

    public string? LastOfferedVersion { get; init; }
}

public sealed record ProfileSettings(
    Guid Id,
    string Name,
    string? CodexHome,
    bool Enabled,
    bool MonitorInBackground)
{
    public ProfileDefinition ToDefinition() => new(Id, Name, CodexHome, Enabled, MonitorInBackground);

    public static ProfileSettings FromDefinition(ProfileDefinition profile) =>
        new(profile.Id, profile.Name, profile.CodexHome, profile.Enabled, profile.MonitorInBackground);
}
