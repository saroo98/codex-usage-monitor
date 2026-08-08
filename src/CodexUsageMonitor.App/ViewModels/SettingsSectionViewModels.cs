using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Mail;
using CodexUsageMonitor.App.Infrastructure;
using CodexUsageMonitor.Core.Profiles;
using CodexUsageMonitor.Core.Settings;
using CodexUsageMonitor.Core.Usage;
using CodexUsageMonitor.Persistence.History;

namespace CodexUsageMonitor.App.ViewModels;

public abstract class ValidatedSettingsSectionViewModel : ObservableObject
{
    private string? _validationMessage;

    public string? ValidationMessage
    {
        get => _validationMessage;
        protected set
        {
            if (SetProperty(ref _validationMessage, value))
            {
                OnPropertyChanged(nameof(IsValid));
            }
        }
    }

    public bool IsValid => string.IsNullOrWhiteSpace(ValidationMessage);
}

public sealed class GeneralSettingsSectionViewModel : ObservableObject
{
    private bool _startWithWindows;
    private bool _closeToTray;
    private bool _launchMinimized;
    private bool _privacyMode;
    private string _language = "en";

    public bool StartWithWindows { get => _startWithWindows; set => SetProperty(ref _startWithWindows, value); }
    public bool CloseToTray { get => _closeToTray; set => SetProperty(ref _closeToTray, value); }
    public bool LaunchMinimized { get => _launchMinimized; set => SetProperty(ref _launchMinimized, value); }
    public bool PrivacyMode { get => _privacyMode; set => SetProperty(ref _privacyMode, value); }
    public string Language { get => _language; set => SetProperty(ref _language, value); }

    internal void Load(GeneralSettings settings)
    {
        StartWithWindows = settings.StartWithWindows;
        CloseToTray = settings.CloseToTray;
        LaunchMinimized = settings.LaunchMinimized;
        PrivacyMode = settings.PrivacyMode;
        Language = settings.Language;
    }

    internal GeneralSettings ApplyTo(GeneralSettings current) => current with
    {
        StartWithWindows = StartWithWindows,
        CloseToTray = CloseToTray,
        LaunchMinimized = LaunchMinimized,
        PrivacyMode = PrivacyMode,
        Language = string.IsNullOrWhiteSpace(Language) ? "en" : Language.Trim(),
    };
}

public sealed class WidgetSettingsSectionViewModel : ObservableObject
{
    private WidgetSize _size;
    private AppTheme _theme;
    private double _opacity;
    private bool _topmost;
    private bool _locked;
    private bool _clickThrough;
    private bool _snapToEdges;
    private bool _allowTaskbarOverlap;
    private bool _reduceMotion;
    private bool _showResetTime;
    private bool _showAccountLabel;

    public Array Sizes { get; } = Enum.GetValues<WidgetSize>();
    public Array Themes { get; } = Enum.GetValues<AppTheme>();
    public WidgetSize Size { get => _size; set => SetProperty(ref _size, value); }
    public AppTheme Theme { get => _theme; set => SetProperty(ref _theme, value); }
    public double Opacity { get => _opacity; set => SetProperty(ref _opacity, Math.Clamp(value, 0.35, 1)); }
    public bool Topmost { get => _topmost; set => SetProperty(ref _topmost, value); }
    public bool Locked { get => _locked; set => SetProperty(ref _locked, value); }
    public bool ClickThrough { get => _clickThrough; set => SetProperty(ref _clickThrough, value); }
    public bool SnapToEdges { get => _snapToEdges; set => SetProperty(ref _snapToEdges, value); }
    public bool AllowTaskbarOverlap { get => _allowTaskbarOverlap; set => SetProperty(ref _allowTaskbarOverlap, value); }
    public bool ReduceMotion { get => _reduceMotion; set => SetProperty(ref _reduceMotion, value); }
    public bool ShowResetTime { get => _showResetTime; set => SetProperty(ref _showResetTime, value); }
    public bool ShowAccountLabel { get => _showAccountLabel; set => SetProperty(ref _showAccountLabel, value); }

    internal void Load(WidgetSettings settings)
    {
        Size = settings.Size;
        Theme = settings.Theme;
        Opacity = settings.Opacity;
        Topmost = settings.Topmost;
        Locked = settings.Locked;
        ClickThrough = settings.ClickThrough;
        SnapToEdges = settings.SnapToEdges;
        AllowTaskbarOverlap = settings.AllowTaskbarOverlap;
        ReduceMotion = settings.ReduceMotion;
        ShowResetTime = settings.ResetTimeDisplay is not ResetTimeDisplayMode.Hidden;
        ShowAccountLabel = settings.ShowAccountLabel;
    }

    internal WidgetSettings ApplyTo(WidgetSettings current) => current with
    {
        Size = Size,
        Theme = Theme,
        Opacity = Opacity,
        Topmost = Topmost,
        Locked = Locked,
        ClickThrough = ClickThrough,
        SnapToEdges = SnapToEdges,
        AllowTaskbarOverlap = AllowTaskbarOverlap,
        ReduceMotion = ReduceMotion,
        ResetTimeDisplay = ShowResetTime ? ResetTimeDisplayMode.Countdown : ResetTimeDisplayMode.Hidden,
        ShowAccountLabel = ShowAccountLabel,
    };
}

public sealed class LimitSettingsSectionViewModel : ObservableObject
{
    private LimitSelectionMode _selectionMode;
    private string? _explicitLimitIdentity;
    private string? _preferredModel;
    private bool _mediumDualMeter;

    public Array Modes { get; } = Enum.GetValues<LimitSelectionMode>();
    public LimitSelectionMode SelectionMode { get => _selectionMode; set => SetProperty(ref _selectionMode, value); }
    public string? ExplicitLimitIdentity { get => _explicitLimitIdentity; set => SetProperty(ref _explicitLimitIdentity, value); }
    public string? PreferredModel { get => _preferredModel; set => SetProperty(ref _preferredModel, value); }
    public bool MediumDualMeter { get => _mediumDualMeter; set => SetProperty(ref _mediumDualMeter, value); }

    internal void Load(LimitSettings settings)
    {
        SelectionMode = settings.SelectionMode;
        ExplicitLimitIdentity = settings.ExplicitLimitIdentity;
        PreferredModel = settings.PreferredModel;
        MediumDualMeter = settings.MediumDualMeter;
    }

    internal LimitSettings ApplyTo(LimitSettings current) => current with
    {
        SelectionMode = SelectionMode,
        ExplicitLimitIdentity = Normalize(ExplicitLimitIdentity),
        PreferredModel = Normalize(PreferredModel),
        MediumDualMeter = MediumDualMeter,
    };

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class NotificationSettingsSectionViewModel : ValidatedSettingsSectionViewModel
{
    private bool _enabled;
    private string _thresholds = "20, 10, 5, 0";
    private bool _notifyOnReset;
    private bool _notifyOnConnectionLoss;
    private bool _playSound;
    private bool _quietHoursEnabled;
    private string _quietHoursStart = "22:00";
    private string _quietHoursEnd = "08:00";

    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public string Thresholds { get => _thresholds; set { if (SetProperty(ref _thresholds, value)) Validate(); } }
    public bool NotifyOnReset { get => _notifyOnReset; set => SetProperty(ref _notifyOnReset, value); }
    public bool NotifyOnConnectionLoss { get => _notifyOnConnectionLoss; set => SetProperty(ref _notifyOnConnectionLoss, value); }
    public bool PlaySound { get => _playSound; set => SetProperty(ref _playSound, value); }
    public bool QuietHoursEnabled { get => _quietHoursEnabled; set { if (SetProperty(ref _quietHoursEnabled, value)) Validate(); } }
    public string QuietHoursStart { get => _quietHoursStart; set { if (SetProperty(ref _quietHoursStart, value)) Validate(); } }
    public string QuietHoursEnd { get => _quietHoursEnd; set { if (SetProperty(ref _quietHoursEnd, value)) Validate(); } }

    internal void Load(NotificationSettings settings)
    {
        Enabled = settings.Enabled;
        Thresholds = string.Join(", ", settings.Thresholds);
        NotifyOnReset = settings.NotifyOnReset;
        NotifyOnConnectionLoss = settings.NotifyOnConnectionLoss;
        PlaySound = settings.PlaySound;
        QuietHoursEnabled = settings.QuietHoursEnabled;
        QuietHoursStart = settings.QuietHoursStart.ToString("HH:mm", CultureInfo.InvariantCulture);
        QuietHoursEnd = settings.QuietHoursEnd.ToString("HH:mm", CultureInfo.InvariantCulture);
        Validate();
    }

    internal NotificationSettings ApplyTo(NotificationSettings current)
    {
        var thresholds = ParseThresholds();
        var start = ParseTime(QuietHoursStart, "start");
        var end = ParseTime(QuietHoursEnd, "end");
        return current with
        {
            Enabled = Enabled,
            Thresholds = thresholds,
            NotifyOnReset = NotifyOnReset,
            NotifyOnConnectionLoss = NotifyOnConnectionLoss,
            PlaySound = PlaySound,
            QuietHoursEnabled = QuietHoursEnabled,
            QuietHoursStart = start,
            QuietHoursEnd = end,
        };
    }

    private void Validate()
    {
        try
        {
            _ = ParseThresholds();
            if (QuietHoursEnabled)
            {
                _ = ParseTime(QuietHoursStart, "start");
                _ = ParseTime(QuietHoursEnd, "end");
            }
            ValidationMessage = null;
        }
        catch (FormatException exception)
        {
            ValidationMessage = exception.Message;
        }
    }

    private int[] ParseThresholds()
    {
        var values = Thresholds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1)
            .Distinct()
            .OrderDescending()
            .ToArray();
        if (values.Length == 0 || values.Any(static value => value is < 0 or > 100))
        {
            throw new FormatException("Enter one or more notification thresholds from 0 to 100, separated by commas.");
        }
        return values;
    }

    private static TimeOnly ParseTime(string value, string label) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : throw new FormatException($"Enter the quiet-hours {label} time in 24-hour HH:mm format.");
}

public sealed record EmailProviderChoice(EmailProviderMode Mode, string DisplayName);

public sealed class EmailSettingsSectionViewModel : ValidatedSettingsSectionViewModel
{
    private EmailProviderMode _provider = EmailProviderMode.Off;
    private bool _enabled;
    private string? _connectedAddress;
    private string? _senderAddress;
    private string? _smtpUsername;
    private SmtpSecurityMode _smtpSecurity = SmtpSecurityMode.StartTls;
    private string? _smtpHost;
    private int _smtpPort = 587;
    private bool _includeAccountLabel;
    private string _credentialStatus = "Not stored";
    private bool _credentialStored;
    private string _oauthConnectionStatus = "Not connected";
    private bool _oauthConnected;
    private bool _oauthBusy;
    private bool _googleConnectionAvailable;
    private bool _microsoftConnectionAvailable;

    public IReadOnlyList<EmailProviderChoice> Providers { get; } =
    [
        new(EmailProviderMode.Gmail, "Gmail"),
        new(EmailProviderMode.Microsoft365, "Outlook / Microsoft 365"),
        new(EmailProviderMode.ProtonMailBridge, "Proton Mail"),
        new(EmailProviderMode.OtherSmtp, "Other email (SMTP) [Advanced]"),
        new(EmailProviderMode.Off, "Off"),
    ];

    public IReadOnlyList<SmtpSecurityMode> SmtpSecurityModes { get; } =
        [SmtpSecurityMode.StartTls, SmtpSecurityMode.Tls];

    public EmailProviderMode Provider
    {
        get => _provider;
        set
        {
            if (!SetProperty(ref _provider, value)) return;
            if (value is EmailProviderMode.Off) Enabled = false;
            if (value is EmailProviderMode.ProtonMailBridge)
            {
                SmtpHost = "127.0.0.1";
                SmtpPort = 1025;
                SmtpSecurity = SmtpSecurityMode.StartTls;
            }
            RaiseProviderProperties();
            Validate();
        }
    }

    public bool Enabled { get => _enabled; set { if (SetProperty(ref _enabled, value)) Validate(); } }
    public string? SenderAddress { get => _senderAddress; set { if (SetProperty(ref _senderAddress, value)) Validate(); } }
    public string? SmtpHost { get => _smtpHost; set { if (SetProperty(ref _smtpHost, value)) Validate(); } }
    public int SmtpPort { get => _smtpPort; set => SetProperty(ref _smtpPort, Math.Clamp(value, 1, 65535)); }
    public string? SmtpUsername { get => _smtpUsername; set { if (SetProperty(ref _smtpUsername, value)) Validate(); } }
    public SmtpSecurityMode SmtpSecurity { get => _smtpSecurity; set { if (SetProperty(ref _smtpSecurity, value)) Validate(); } }
    public bool IncludeAccountLabel { get => _includeAccountLabel; set => SetProperty(ref _includeAccountLabel, value); }
    public bool IsOtherSmtp => Provider is EmailProviderMode.OtherSmtp;
    public bool IsProtonBridge => Provider is EmailProviderMode.ProtonMailBridge;
    public bool IsSmtpProvider => IsOtherSmtp || IsProtonBridge;
    public bool IsOAuthProvider => Provider is EmailProviderMode.Microsoft365 or EmailProviderMode.Gmail;
    public bool IsMicrosoftOAuth => Provider is EmailProviderMode.Microsoft365;
    public bool ShowOAuthConnect => IsOAuthProvider && !OAuthConnected;
    public bool ShowOAuthConnected => IsOAuthProvider && OAuthConnected;
    public string OAuthConnectButtonText => Provider is EmailProviderMode.Gmail ? "Connect with Google" : "Connect with Microsoft";
    public string ConnectedAsText => string.IsNullOrWhiteSpace(_connectedAddress) ? "Not connected" : $"Connected as {_connectedAddress}";
    public string CredentialStatus { get => _credentialStatus; internal set => SetProperty(ref _credentialStatus, value); }
    public bool CredentialStored { get => _credentialStored; internal set => SetProperty(ref _credentialStored, value); }
    public string OAuthConnectionStatus { get => _oauthConnectionStatus; internal set => SetProperty(ref _oauthConnectionStatus, value); }

    public bool GoogleConnectionAvailable
    {
        get => _googleConnectionAvailable;
        set { if (SetProperty(ref _googleConnectionAvailable, value)) OnPropertyChanged(nameof(CanChangeOAuthConnection)); }
    }

    public bool MicrosoftConnectionAvailable
    {
        get => _microsoftConnectionAvailable;
        set { if (SetProperty(ref _microsoftConnectionAvailable, value)) OnPropertyChanged(nameof(CanChangeOAuthConnection)); }
    }

    public bool OAuthConnected
    {
        get => _oauthConnected;
        internal set
        {
            if (!SetProperty(ref _oauthConnected, value)) return;
            OnPropertyChanged(nameof(CanDisconnectOAuth));
            OnPropertyChanged(nameof(ShowOAuthConnect));
            OnPropertyChanged(nameof(ShowOAuthConnected));
            Validate();
        }
    }

    public bool OAuthBusy
    {
        get => _oauthBusy;
        set
        {
            if (!SetProperty(ref _oauthBusy, value)) return;
            OnPropertyChanged(nameof(CanChangeOAuthConnection));
            OnPropertyChanged(nameof(CanDisconnectOAuth));
        }
    }

    public bool CanChangeOAuthConnection => IsOAuthProvider && !OAuthBusy &&
        (Provider is EmailProviderMode.Gmail ? GoogleConnectionAvailable : MicrosoftConnectionAvailable);
    public bool CanDisconnectOAuth => OAuthConnected && !OAuthBusy;

    internal void SetConnectedAddress(string? address)
    {
        _connectedAddress = Normalize(address);
        OAuthConnected = MailAddress.TryCreate(_connectedAddress, out _);
        OnPropertyChanged(nameof(ConnectedAsText));
        Validate();
    }

    internal void Load(EmailSettings settings)
    {
        Provider = settings.Provider;
        Enabled = settings.Enabled;
        _connectedAddress = settings.ConnectedAddress;
        SenderAddress = settings.SenderAddress;
        SmtpHost = settings.SmtpHost;
        SmtpPort = settings.SmtpPort;
        SmtpUsername = settings.SmtpUsername;
        SmtpSecurity = settings.SmtpSecurity;
        IncludeAccountLabel = settings.IncludeAccountLabel;
        OAuthConnected = MailAddress.TryCreate(_connectedAddress, out _);
        OnPropertyChanged(nameof(ConnectedAsText));
        Validate();
    }

    internal EmailSettings ApplyTo(EmailSettings current, bool keepSmtpCredential, bool keepOAuthTokens)
    {
        var oauth = IsOAuthProvider;
        var smtp = IsSmtpProvider;
        return current with
        {
            Provider = Provider,
            Enabled = Provider is not EmailProviderMode.Off && Enabled,
            ConnectedAddress = oauth && keepOAuthTokens ? current.ConnectedAddress : null,
            SenderAddress = smtp ? Normalize(SenderAddress) : null,
            Recipients = [],
            SmtpHost = smtp ? Normalize(SmtpHost) : null,
            SmtpPort = SmtpPort,
            SmtpUsername = smtp ? Normalize(SmtpUsername) : null,
            SmtpSecurity = smtp ? SmtpSecurity : SmtpSecurityMode.StartTls,
            CredentialReference = smtp && keepSmtpCredential ? current.CredentialReference : null,
            OAuthClientId = oauth && keepOAuthTokens ? current.OAuthClientId : null,
            OAuthTenant = oauth && keepOAuthTokens ? current.OAuthTenant : "common",
            OAuthTokenReference = oauth && keepOAuthTokens ? current.OAuthTokenReference : null,
            OAuthRegistrationId = oauth && keepOAuthTokens ? current.OAuthRegistrationId : null,
            IncludeAccountLabel = IncludeAccountLabel,
        };
    }

    private void Validate()
    {
        if (Provider is EmailProviderMode.Off)
        {
            ValidationMessage = null;
        }
        else if (IsOAuthProvider)
        {
            ValidationMessage = MailAddress.TryCreate(_connectedAddress, out _)
                ? null
                : "Connect the selected email account before enabling notifications.";
        }
        else if (!MailAddress.TryCreate(SenderAddress, out _))
        {
            ValidationMessage = "Enter the email address that owns this SMTP account.";
        }
        else if (string.IsNullOrWhiteSpace(SmtpHost))
        {
            ValidationMessage = "Enter the SMTP server host.";
        }
        else if (SmtpSecurity is SmtpSecurityMode.None)
        {
            ValidationMessage = "Encrypted SMTP transport is required.";
        }
        else if (IsProtonBridge && SmtpHost is not ("127.0.0.1" or "::1" or "localhost"))
        {
            ValidationMessage = "Proton Mail Bridge must use the local loopback service.";
        }
        else if (IsProtonBridge && string.IsNullOrWhiteSpace(SmtpUsername))
        {
            ValidationMessage = "Enter the username generated by Proton Mail Bridge.";
        }
        else
        {
            ValidationMessage = null;
        }

        OnPropertyChanged(nameof(CanChangeOAuthConnection));
    }

    private void RaiseProviderProperties()
    {
        OnPropertyChanged(nameof(IsOtherSmtp));
        OnPropertyChanged(nameof(IsProtonBridge));
        OnPropertyChanged(nameof(IsSmtpProvider));
        OnPropertyChanged(nameof(IsOAuthProvider));
        OnPropertyChanged(nameof(IsMicrosoftOAuth));
        OnPropertyChanged(nameof(ShowOAuthConnect));
        OnPropertyChanged(nameof(ShowOAuthConnected));
        OnPropertyChanged(nameof(OAuthConnectButtonText));
        OnPropertyChanged(nameof(CanChangeOAuthConnection));
        OnPropertyChanged(nameof(CanDisconnectOAuth));
    }

    internal static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class AccountsSettingsSectionViewModel : ObservableObject
{
    private ProfileEditorViewModel? _selectedProfile;

    public ObservableCollection<ProfileEditorViewModel> Profiles { get; } = [];
    public ProfileEditorViewModel? SelectedProfile { get => _selectedProfile; set { if (SetProperty(ref _selectedProfile, value)) OnPropertyChanged(nameof(CanRemove)); } }
    public bool CanRemove => SelectedProfile is not null && Profiles.Count > 1;

    internal void Load(IReadOnlyList<ProfileSettings> profiles)
    {
        Profiles.Clear();
        foreach (var profile in profiles) Profiles.Add(new ProfileEditorViewModel(profile));
        SelectedProfile = Profiles.FirstOrDefault();
        OnPropertyChanged(nameof(CanRemove));
    }

    internal void Add()
    {
        var profile = new ProfileEditorViewModel(ProfileSettings.FromDefinition(new ProfileDefinition(
            Guid.NewGuid(), $"Profile {Profiles.Count + 1}", null, true, true)));
        Profiles.Add(profile);
        SelectedProfile = profile;
        OnPropertyChanged(nameof(CanRemove));
    }

    internal void Remove()
    {
        if (!CanRemove || SelectedProfile is null) return;
        var index = Profiles.IndexOf(SelectedProfile);
        Profiles.Remove(SelectedProfile);
        SelectedProfile = Profiles[Math.Clamp(index, 0, Profiles.Count - 1)];
        OnPropertyChanged(nameof(CanRemove));
    }

    internal ProfileSettings[] BuildProfiles()
    {
        var profiles = Profiles.Select(static profile => profile.ToSettings()).ToArray();
        return profiles.Length > 0 ? profiles : throw new InvalidOperationException("At least one profile is required.");
    }
}

public sealed class HistorySettingsSectionViewModel : ObservableObject
{
    private bool _enabled;
    private int _retentionDays;
    private int _sampleIntervalMinutes;
    private IReadOnlyList<HistoryPoint> _points = [];

    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public int RetentionDays { get => _retentionDays; set => SetProperty(ref _retentionDays, Math.Clamp(value, 7, 365)); }
    public int SampleIntervalMinutes { get => _sampleIntervalMinutes; set => SetProperty(ref _sampleIntervalMinutes, Math.Clamp(value, 1, 60)); }
    public IReadOnlyList<HistoryPoint> Points { get => _points; internal set => SetProperty(ref _points, value); }

    internal void Load(HistorySettings settings)
    {
        Enabled = settings.Enabled;
        RetentionDays = settings.RetentionDays;
        SampleIntervalMinutes = settings.SampleIntervalMinutes;
    }

    internal HistorySettings ApplyTo(HistorySettings current) => current with
    {
        Enabled = Enabled,
        RetentionDays = RetentionDays,
        SampleIntervalMinutes = SampleIntervalMinutes,
    };
}

public sealed class UpdateSettingsSectionViewModel : ObservableObject
{
    private bool _automaticChecks;
    private bool _automaticDownload;
    private bool _installOnExit;
    private UpdateChannel _channel;
    private int _checkIntervalHours;
    private string _status = "Update status has not been checked in this session.";
    private string _currentVersion = "Unknown";
    private string? _availableVersion;
    private string _lastChecked = "Never";
    private string _progress = string.Empty;
    private bool _canPrepare;
    private bool _canInstall;

    public Array Channels { get; } = Enum.GetValues<UpdateChannel>();
    public bool AutomaticChecks { get => _automaticChecks; set => SetProperty(ref _automaticChecks, value); }
    public bool AutomaticDownload { get => _automaticDownload; set => SetProperty(ref _automaticDownload, value); }
    public bool InstallOnExit { get => _installOnExit; set => SetProperty(ref _installOnExit, value); }
    public UpdateChannel Channel { get => _channel; set => SetProperty(ref _channel, value); }
    public int CheckIntervalHours { get => _checkIntervalHours; set => SetProperty(ref _checkIntervalHours, Math.Clamp(value, 1, 168)); }
    public string Status { get => _status; internal set => SetProperty(ref _status, value); }
    public string CurrentVersion { get => _currentVersion; internal set => SetProperty(ref _currentVersion, value); }
    public string? AvailableVersion { get => _availableVersion; internal set => SetProperty(ref _availableVersion, value); }
    public string LastChecked { get => _lastChecked; internal set => SetProperty(ref _lastChecked, value); }
    public string Progress { get => _progress; internal set => SetProperty(ref _progress, value); }
    public bool CanPrepare { get => _canPrepare; internal set => SetProperty(ref _canPrepare, value); }
    public bool CanInstall { get => _canInstall; internal set => SetProperty(ref _canInstall, value); }

    internal void Load(UpdateSettings settings)
    {
        AutomaticChecks = settings.AutomaticChecks;
        AutomaticDownload = settings.AutomaticDownload;
        InstallOnExit = settings.InstallOnExit;
        Channel = settings.Channel;
        CheckIntervalHours = settings.CheckIntervalHours;
    }

    internal UpdateSettings ApplyTo(UpdateSettings current) => current with
    {
        AutomaticChecks = AutomaticChecks,
        AutomaticDownload = AutomaticDownload,
        InstallOnExit = InstallOnExit,
        Channel = Channel,
        CheckIntervalHours = CheckIntervalHours,
    };
}

public sealed class DiagnosticsSettingsSectionViewModel : ObservableObject
{
    private string _summary = "Diagnostics have not been run in this session.";
    private string _migrationSummary = "Checking for an existing Codex Usage Notifier installation…";
    private bool _canRetireLegacyTasks;
    private bool _canRestoreLegacyTasks;
    private bool _hasVerifiedMigrationBackup;

    public string Summary { get => _summary; internal set => SetProperty(ref _summary, value); }
    public string MigrationSummary { get => _migrationSummary; internal set => SetProperty(ref _migrationSummary, value); }
    public bool CanRetireLegacyTasks { get => _canRetireLegacyTasks; internal set => SetProperty(ref _canRetireLegacyTasks, value); }
    public bool CanRestoreLegacyTasks { get => _canRestoreLegacyTasks; internal set => SetProperty(ref _canRestoreLegacyTasks, value); }
    public bool HasVerifiedMigrationBackup { get => _hasVerifiedMigrationBackup; internal set => SetProperty(ref _hasVerifiedMigrationBackup, value); }
}
