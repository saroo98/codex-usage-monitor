using System.Globalization;
using System.Windows.Input;
using CodexUsageMonitor.App.Infrastructure;
using CodexUsageMonitor.App.Services;
using CodexUsageMonitor.Core.Diagnostics;
using CodexUsageMonitor.Core.Profiles;
using CodexUsageMonitor.Core.Settings;
using CodexUsageMonitor.Core.Usage;
using CodexUsageMonitor.Persistence.History;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.App.ViewModels;

public enum SettingsSection
{
    General,
    Widget,
    Limits,
    Notifications,
    Email,
    Accounts,
    History,
    Updates,
    Diagnostics,
}

public sealed record SettingsActions(
    Func<CancellationToken, Task<IReadOnlyList<HistoryPoint>>> LoadHistory,
    Func<string, CancellationToken, Task<string>> ExportSupportBundle,
    Func<CancellationToken, Task<string>> RunDatabaseIntegrity,
    Func<CancellationToken, Task<string>> CaptureDiagnostics,
    Func<CancellationToken, Task> TestNotification,
    Func<CancellationToken, Task> TestEmail,
    Func<CancellationToken, Task<UpdateRuntimeSnapshot>> CheckForUpdates,
    Func<CancellationToken, Task<UpdateRuntimeSnapshot>> PrepareUpdate,
    Func<bool> ConfirmInstallUpdate,
    Func<CancellationToken, Task<UpdateRuntimeSnapshot>> InstallUpdate,
    Func<UpdateRuntimeSnapshot> GetUpdateStatus,
    Action OpenLogsFolder,
    Func<CancellationToken, Task<string>> RestartConnection,
    Func<CancellationToken, Task<string>> ReconcileStartup,
    Func<CancellationToken, Task<string>> ResetWidgetPosition,
    Action RebuildTrayIcon,
    Func<CancellationToken, Task<LegacyMigrationSummary>> LoadMigrationStatus,
    Func<bool, CancellationToken, Task<LegacyMigrationOperationResult>> RetireLegacyTasks,
    Func<bool, CancellationToken, Task<LegacyMigrationOperationResult>> RestoreLegacyTasks,
    Action OpenMigrationBackupFolder);

public sealed class ProfileEditorViewModel : ObservableObject
{
    private Guid _id;
    private string _name;
    private string? _codexHome;
    private bool _enabled;
    private bool _monitorInBackground;

    public ProfileEditorViewModel(ProfileSettings profile)
    {
        _id = profile.Id;
        _name = profile.Name;
        _codexHome = profile.CodexHome;
        _enabled = profile.Enabled;
        _monitorInBackground = profile.MonitorInBackground;
    }

    public Guid Id { get => _id; set => SetProperty(ref _id, value); }
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string? CodexHome { get => _codexHome; set => SetProperty(ref _codexHome, value); }
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public bool MonitorInBackground { get => _monitorInBackground; set => SetProperty(ref _monitorInBackground, value); }

    public ProfileSettings ToSettings() => new(
        Id == Guid.Empty ? Guid.NewGuid() : Id,
        string.IsNullOrWhiteSpace(Name) ? "Codex profile" : Name.Trim(),
        string.IsNullOrWhiteSpace(CodexHome) ? null : CodexHome.Trim(),
        Enabled,
        MonitorInBackground);
}

public sealed class SettingsViewModel : ObservableObject
{
    private readonly ApplicationSettingsService _settingsService;
    private readonly SettingsActions _actions;
    private readonly EmailCredentialService _emailCredentials;
    private readonly OAuthConnectionService _oauthConnections;
    private readonly ILogger<SettingsViewModel> _logger;
    private SettingsSection _selectedSection;
    private string _statusMessage = "Changes are saved only when you select Save.";

    public SettingsViewModel(
        ApplicationSettingsService settingsService,
        SettingsActions actions,
        EmailCredentialService emailCredentials,
        OAuthConnectionService oauthConnections,
        ILogger<SettingsViewModel> logger)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _emailCredentials = emailCredentials ?? throw new ArgumentNullException(nameof(emailCredentials));
        _oauthConnections = oauthConnections ?? throw new ArgumentNullException(nameof(oauthConnections));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Sections = Enum.GetValues<SettingsSection>();
        General = new GeneralSettingsSectionViewModel();
        Widget = new WidgetSettingsSectionViewModel();
        Limits = new LimitSettingsSectionViewModel();
        Notifications = new NotificationSettingsSectionViewModel();
        Email = new EmailSettingsSectionViewModel();
        Accounts = new AccountsSettingsSectionViewModel();
        History = new HistorySettingsSectionViewModel();
        Updates = new UpdateSettingsSectionViewModel();
        Diagnostics = new DiagnosticsSettingsSectionViewModel();
        SaveCommand = new AsyncRelayCommand(SaveAsync, onError: ReportCommandFailure);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(this, false));
        AddProfileCommand = new RelayCommand(AddProfile);
        RemoveProfileCommand = new RelayCommand(RemoveProfile, () => Accounts.CanRemove);
        ReloadHistoryCommand = new AsyncRelayCommand(ReloadHistoryAsync, onError: ReportCommandFailure);
        DatabaseIntegrityCommand = new AsyncRelayCommand(RunDatabaseIntegrityAsync, onError: ReportCommandFailure);
        RefreshDiagnosticsCommand = new AsyncRelayCommand(RefreshDiagnosticsAsync, onError: ReportCommandFailure);
        TestNotificationCommand = new AsyncRelayCommand(TestNotificationAsync, onError: ReportCommandFailure);
        TestEmailCommand = new AsyncRelayCommand(TestEmailAsync, onError: ReportCommandFailure);
        CheckForUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesAsync, onError: ReportCommandFailure);
        PrepareUpdateCommand = new AsyncRelayCommand(PrepareUpdateAsync, () => Updates.CanPrepare, ReportCommandFailure);
        InstallUpdateCommand = new AsyncRelayCommand(InstallUpdateAsync, () => Updates.CanInstall, ReportCommandFailure);
        OpenLogsCommand = new RelayCommand(OpenLogsFolder);
        RestartConnectionCommand = new AsyncRelayCommand(RestartConnectionAsync, onError: ReportCommandFailure);
        ReconcileStartupCommand = new AsyncRelayCommand(ReconcileStartupAsync, onError: ReportCommandFailure);
        ResetWidgetPositionCommand = new AsyncRelayCommand(ResetWidgetPositionAsync, onError: ReportCommandFailure);
        RebuildTrayIconCommand = new RelayCommand(RebuildTrayIcon);
        RefreshMigrationStatusCommand = new AsyncRelayCommand(RefreshMigrationStatusAsync, onError: ReportCommandFailure);
        OpenMigrationBackupCommand = new RelayCommand(_actions.OpenMigrationBackupFolder);
        Load(_settingsService.Current);
        ApplyUpdateSnapshot(_actions.GetUpdateStatus());
    }

    public event EventHandler<bool>? RequestClose;

    public IReadOnlyList<SettingsSection> Sections { get; }
    public GeneralSettingsSectionViewModel General { get; }
    public WidgetSettingsSectionViewModel Widget { get; }
    public LimitSettingsSectionViewModel Limits { get; }
    public NotificationSettingsSectionViewModel Notifications { get; }
    public EmailSettingsSectionViewModel Email { get; }
    public AccountsSettingsSectionViewModel Accounts { get; }
    public HistorySettingsSectionViewModel History { get; }
    public UpdateSettingsSectionViewModel Updates { get; }
    public DiagnosticsSettingsSectionViewModel Diagnostics { get; }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand AddProfileCommand { get; }
    public ICommand RemoveProfileCommand { get; }
    public ICommand ReloadHistoryCommand { get; }
    public ICommand DatabaseIntegrityCommand { get; }
    public ICommand RefreshDiagnosticsCommand { get; }
    public ICommand TestNotificationCommand { get; }
    public ICommand TestEmailCommand { get; }
    public ICommand CheckForUpdatesCommand { get; }
    public ICommand PrepareUpdateCommand { get; }
    public ICommand InstallUpdateCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand RestartConnectionCommand { get; }
    public ICommand ReconcileStartupCommand { get; }
    public ICommand ResetWidgetPositionCommand { get; }
    public ICommand RebuildTrayIconCommand { get; }
    public ICommand RefreshMigrationStatusCommand { get; }
    public ICommand OpenMigrationBackupCommand { get; }

    public SettingsSection SelectedSection { get => _selectedSection; set => SetProperty(ref _selectedSection, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    private void Load(AppSettings settings)
    {
        General.Load(settings.General);
        Widget.Load(settings.Widget);
        Limits.Load(settings.Limits);
        Notifications.Load(settings.Notifications);
        Email.Load(settings.Email);
        Accounts.Load(settings.Profiles);
        History.Load(settings.History);
        Updates.Load(settings.Updates);
        ((RelayCommand)RemoveProfileCommand).RaiseCanExecuteChanged();
    }

    public void SetEmailCredentialStatus(EmailCredentialStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        Email.CredentialStored = status.IsStored;
        Email.CredentialStatus = status.SafeMessageCode switch
        {
            "email.password_stored" => "Stored securely in Windows Credential Manager",
            "email.password_stored_cleanup_pending" => "Stored; an obsolete credential could not yet be removed",
            _ => "Not stored",
        };
    }

    public void SetOAuthConnectionStatus(OAuthConnectionStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        Email.OAuthConnected = status.IsConnected;
        Email.OAuthConnectionStatus = status.SafeMessageCode switch
        {
            "email.oauth_connected" => "Connected securely",
            "email.oauth_connected_cleanup_pending" => "Connected; an obsolete credential could not yet be removed",
            "email.oauth_status_unavailable" => "Connection status unavailable",
            _ => "Not connected",
        };
    }

    public void ReportEmailOperationFailure(string safeMessage)
    {
        StatusMessage = SafeDiagnosticRedactor.Redact(safeMessage);
    }

    public void ReportOperationStatus(string safeMessage)
    {
        StatusMessage = SafeDiagnosticRedactor.Redact(safeMessage);
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            var previousEmail = _settingsService.Current.Email;
            if (!Notifications.IsValid) throw new FormatException(Notifications.ValidationMessage);
            if (!Email.IsValid) throw new FormatException(Email.ValidationMessage);
            var normalizedSender = EmailSettingsSectionViewModel.Normalize(Email.SenderAddress);
            var sameSender = string.Equals(previousEmail.SenderAddress, normalizedSender, StringComparison.OrdinalIgnoreCase);
            var keepSmtpCredential = Email.Provider is EmailProviderMode.GenericSmtp &&
                previousEmail.Provider is EmailProviderMode.GenericSmtp && sameSender;
            var keepOAuthTokens = Email.Provider is EmailProviderMode.MicrosoftOAuth or EmailProviderMode.GoogleOAuth &&
                previousEmail.Provider == Email.Provider && sameSender &&
                string.Equals(previousEmail.OAuthClientId, Email.OAuthClientId?.Trim(), StringComparison.Ordinal);
            var profiles = Accounts.BuildProfiles();
            await _settingsService.UpdateAsync(current => current with
            {
                General = General.ApplyTo(current.General),
                Widget = Widget.ApplyTo(current.Widget),
                Limits = Limits.ApplyTo(current.Limits),
                Notifications = Notifications.ApplyTo(current.Notifications),
                Email = Email.ApplyTo(current.Email, keepSmtpCredential, keepOAuthTokens),
                History = History.ApplyTo(current.History),
                Updates = Updates.ApplyTo(current.Updates),
                Profiles = profiles,
            }, cancellationToken);
            if (!keepSmtpCredential && !string.IsNullOrWhiteSpace(previousEmail.CredentialReference))
            {
                try
                {
                    await _emailCredentials.DeleteReferenceAsync(previousEmail.CredentialReference, cancellationToken);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    StatusMessage = "Settings saved; an obsolete SMTP credential will require manual cleanup.";
                    RequestClose?.Invoke(this, true);
                    return;
                }
            }

            if (!keepOAuthTokens && !string.IsNullOrWhiteSpace(previousEmail.OAuthTokenReference))
            {
                try
                {
                    await _oauthConnections.DeleteReferenceAsync(previousEmail.OAuthTokenReference, cancellationToken);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    StatusMessage = "Settings saved; an obsolete OAuth token will require manual cleanup.";
                    RequestClose?.Invoke(this, true);
                    return;
                }
            }

            StatusMessage = "Settings saved.";
            RequestClose?.Invoke(this, true);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Cannot save: {SafeDiagnosticRedactor.Redact(exception.Message)}";
        }
    }

    private void AddProfile()
    {
        Accounts.Add();
        ((RelayCommand)RemoveProfileCommand).RaiseCanExecuteChanged();
    }

    private void RemoveProfile()
    {
        Accounts.Remove();
        ((RelayCommand)RemoveProfileCommand).RaiseCanExecuteChanged();
    }

    private async Task ReloadHistoryAsync(CancellationToken cancellationToken)
    {
        History.Points = await _actions.LoadHistory(cancellationToken);
        StatusMessage = History.Points.Count == 0 ? "No history is available for the active limit yet." : $"Loaded {History.Points.Count} history points.";
    }

    public async Task ExportSupportBundleToAsync(string destinationPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var path = await _actions.ExportSupportBundle(destinationPath, cancellationToken);
        StatusMessage = $"Support bundle created: {path}";
    }

    private async Task RunDatabaseIntegrityAsync(CancellationToken cancellationToken)
    {
        Diagnostics.Summary = $"Database integrity: {await _actions.RunDatabaseIntegrity(cancellationToken)}";
        StatusMessage = "Database integrity check completed.";
    }

    public async Task RefreshDiagnosticsAsync(CancellationToken cancellationToken)
    {
        try
        {
            Diagnostics.Summary = await _actions.CaptureDiagnostics(cancellationToken);
            StatusMessage = "Diagnostics refreshed.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "Diagnostics refresh was cancelled.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            Diagnostics.Summary = "Diagnostics are temporarily unavailable.";
            StatusMessage = $"Diagnostics refresh failed safely: {SafeDiagnosticRedactor.Redact(exception.Message)}";
        }
    }

    private async Task RestartConnectionAsync(CancellationToken cancellationToken)
    {
        StatusMessage = await _actions.RestartConnection(cancellationToken);
        Diagnostics.Summary = await _actions.CaptureDiagnostics(cancellationToken);
    }

    private async Task ReconcileStartupAsync(CancellationToken cancellationToken)
    {
        StatusMessage = await _actions.ReconcileStartup(cancellationToken);
        Diagnostics.Summary = await _actions.CaptureDiagnostics(cancellationToken);
    }

    private async Task ResetWidgetPositionAsync(CancellationToken cancellationToken)
    {
        StatusMessage = await _actions.ResetWidgetPosition(cancellationToken);
    }

    private void RebuildTrayIcon()
    {
        try
        {
            _actions.RebuildTrayIcon();
            StatusMessage = "The notification-area icon was rebuilt from the current state.";
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            ReportCommandFailure(exception);
        }
    }

    private void OpenLogsFolder()
    {
        try
        {
            _actions.OpenLogsFolder();
            StatusMessage = "The log folder was opened.";
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            ReportCommandFailure(exception);
        }
    }

    private async Task TestNotificationAsync(CancellationToken cancellationToken) { await _actions.TestNotification(cancellationToken); StatusMessage = "Test notification requested."; }
    private async Task TestEmailAsync(CancellationToken cancellationToken) { await _actions.TestEmail(cancellationToken); StatusMessage = "Test email completed."; }
    private async Task CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        ApplyUpdateSnapshot(await _actions.CheckForUpdates(cancellationToken));
        StatusMessage = Updates.Status;
    }

    private async Task PrepareUpdateAsync(CancellationToken cancellationToken)
    {
        ApplyUpdateSnapshot(await _actions.PrepareUpdate(cancellationToken));
        StatusMessage = Updates.Status;
    }

    private async Task InstallUpdateAsync(CancellationToken cancellationToken)
    {
        if (!_actions.ConfirmInstallUpdate())
        {
            StatusMessage = "Update installation was cancelled. No files were changed.";
            return;
        }

        ApplyUpdateSnapshot(await _actions.InstallUpdate(cancellationToken));
        StatusMessage = Updates.Status;
    }

    private void ApplyUpdateSnapshot(UpdateRuntimeSnapshot snapshot)
    {
        Updates.CurrentVersion = snapshot.CurrentVersion;
        Updates.AvailableVersion = snapshot.AvailableVersion;
        Updates.LastChecked = snapshot.LastCheckedAtUtc is null
            ? "Never"
            : snapshot.LastCheckedAtUtc.Value.ToLocalTime().ToString("G", CultureInfo.CurrentCulture);
        Updates.Progress = snapshot.Progress is null
            ? string.Empty
            : $"{Math.Round(snapshot.Progress.Value * 100, MidpointRounding.AwayFromZero):0}%";
        Updates.CanPrepare = snapshot.CanPrepare;
        Updates.CanInstall = snapshot.CanInstall;
        ((AsyncRelayCommand)PrepareUpdateCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)InstallUpdateCommand).RaiseCanExecuteChanged();
        Updates.Status = snapshot.Status switch
        {
            UpdateRuntimeStatus.NotConfigured => "This build has no update manifest configured.",
            UpdateRuntimeStatus.Ready => "Ready to check for a verified update.",
            UpdateRuntimeStatus.Checking => "Checking the signed update manifest…",
            UpdateRuntimeStatus.Current => $"Version {snapshot.CurrentVersion} is current.",
            UpdateRuntimeStatus.Available => $"Version {snapshot.AvailableVersion} is available and its manifest signature has been verified.",
            UpdateRuntimeStatus.Downloading => $"Downloading and validating version {snapshot.AvailableVersion}…",
            UpdateRuntimeStatus.Staged => $"Version {snapshot.AvailableVersion} is verified and ready to install.",
            UpdateRuntimeStatus.Installing => "The verified updater is starting. The application will close safely.",
            UpdateRuntimeStatus.Recovering => "An interrupted portable update is being rolled back safely.",
            UpdateRuntimeStatus.ManagedExternally => "This packaged installation is updated by Windows App Installer or the Microsoft Store.",
            UpdateRuntimeStatus.UnsupportedOperatingSystem => "The available update does not support this Windows build.",
            UpdateRuntimeStatus.UnsupportedArchitecture => "The available update does not support this processor architecture.",
            UpdateRuntimeStatus.Failed => UpdateFailureMessage(snapshot.SafeErrorCode),
            _ => "Update status is unavailable.",
        };
    }

    private static string UpdateFailureMessage(string? code) => code switch
    {
        "update.network_failed" => "The update service could not be reached. The current installation was not changed.",
        "update.trust_failed" => "Update verification failed. Nothing was downloaded or installed.",
        "update.invalid_data" => "The update metadata or package was invalid. Nothing was installed.",
        "update.access_denied" => "Windows denied access to the update staging area.",
        "update.io_failed" => "The update could not be staged safely because of a storage error.",
        "update.manifest_not_configured" => "This build has no update manifest configured.",
        "update.no_verified_asset" => "No verified update package is ready to download.",
        "update.not_staged" => "Download and verify the update before installing it.",
        _ => "The update operation failed safely. The current installation was not changed.",
    };

    private void ReportCommandFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (IsExpectedOperationFailure(exception))
        {
            _logger.LogWarning(exception, "A settings operation failed safely.");
        }
        else
        {
            _logger.LogError(exception, "An unexpected settings operation failure was contained.");
        }

        StatusMessage = exception switch
        {
            UnauthorizedAccessException => "Windows denied access. No protected setting or file was changed.",
            IOException => "The operation could not complete because of a storage or process I/O error.",
            System.Net.Http.HttpRequestException => "The network operation failed safely. No local installation data was changed.",
            InvalidDataException => "The operation rejected invalid or untrusted data.",
            ArgumentException => "One or more values are invalid. Review the highlighted settings and try again.",
            System.ComponentModel.Win32Exception => "A Windows integration operation failed safely.",
            InvalidOperationException => "The operation is not available in the current application state.",
            _ => "The operation failed safely. Review Diagnostics for the recorded error code.",
        };
    }

    private static bool IsExpectedOperationFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or
        ArgumentException or System.ComponentModel.Win32Exception or System.Net.Http.HttpRequestException;

    public async Task RefreshMigrationStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            ApplyMigrationSummary(await _actions.LoadMigrationStatus(cancellationToken));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            Diagnostics.MigrationSummary = "Migration status is unavailable. No legacy tasks were changed.";
            Diagnostics.CanRetireLegacyTasks = false;
            Diagnostics.CanRestoreLegacyTasks = false;
            Diagnostics.HasVerifiedMigrationBackup = false;
            StatusMessage = $"Migration status failed safely: {SafeDiagnosticRedactor.Redact(exception.Message)}";
        }
    }

    public async Task RetireLegacyTasksAsync(bool explicitlyConfirmed, CancellationToken cancellationToken)
    {
        var result = await _actions.RetireLegacyTasks(explicitlyConfirmed, cancellationToken);
        ApplyMigrationSummary(result.Summary);
        StatusMessage = MigrationMessage(result.SafeStatusCode);
    }

    public async Task RestoreLegacyTasksAsync(bool explicitlyConfirmed, CancellationToken cancellationToken)
    {
        var result = await _actions.RestoreLegacyTasks(explicitlyConfirmed, cancellationToken);
        ApplyMigrationSummary(result.Summary);
        StatusMessage = MigrationMessage(result.SafeStatusCode);
    }

    private void ApplyMigrationSummary(LegacyMigrationSummary summary)
    {
        Diagnostics.CanRetireLegacyTasks = summary.CanRetireTasks;
        Diagnostics.CanRestoreLegacyTasks = summary.CanRestoreTasks;
        Diagnostics.HasVerifiedMigrationBackup = summary.BackupVerified;
        var version = string.IsNullOrWhiteSpace(summary.LegacyVersion) ? "unknown version" : $"version {summary.LegacyVersion}";
        Diagnostics.MigrationSummary = summary.SafeStatusCode switch
        {
            "migration.not_found" => "No previous Codex Usage Notifier installation was detected.",
            "migration.status_unavailable" => "Migration status is unavailable. No legacy tasks were changed.",
            "migration.tasks_retired" => $"Legacy {version} settings were imported and its Scheduled Tasks are disabled. They can be restored here.",
            "migration.backup_unverified" => $"Legacy {version} was detected, but its migration backup could not be verified. Task retirement is blocked.",
            "migration.awaiting_fresh_snapshot" => $"Legacy {version} settings were imported. Task retirement will unlock after this app confirms fresh Codex usage data.",
            "migration.ready_to_retire" => $"Legacy {version} settings and a verified backup are available. You may disable the old Scheduled Tasks.",
            "migration.config_missing" => "A legacy installation was detected without a readable configuration. Nothing was changed.",
            "migration.marker_invalid" => "The migration record is invalid. Legacy tasks remain unchanged.",
            _ => $"Legacy migration status: {summary.SafeStatusCode}.",
        };
    }

    private static string MigrationMessage(string code) => code switch
    {
        "migration.tasks_retired" => "Legacy Scheduled Tasks were disabled and can be restored.",
        "migration.tasks_restored" => "Legacy Scheduled Tasks were restored to their captured enabled state.",
        "migration.confirmation_required" => "The migration action was cancelled before confirmation.",
        "migration.awaiting_fresh_snapshot" => "Wait for a fresh live usage reading before retiring the old tasks.",
        "migration.backup_unverified" => "The legacy backup could not be verified, so no tasks were changed.",
        "migration.task_retirement_partial" => "Some legacy tasks could not be disabled. Review Diagnostics before retrying.",
        "migration.task_restore_partial" => "Some legacy tasks could not be restored. Review Diagnostics before retrying.",
        _ => $"Migration action result: {code}.",
    };
}
