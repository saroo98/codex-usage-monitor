using System.Diagnostics;
using System.Windows;
using CodexUsageMonitor.Application.Diagnostics;
using CodexUsageMonitor.App.Services;
using CodexUsageMonitor.App.ViewModels;
using CodexUsageMonitor.Core.Abstractions;
using CodexUsageMonitor.Core.Settings;
using CodexUsageMonitor.Core.Usage;
using CodexUsageMonitor.Email.Models;
using CodexUsageMonitor.Notifications.Native;
using CodexUsageMonitor.Persistence.Database;
using CodexUsageMonitor.Persistence.Diagnostics;
using CodexUsageMonitor.Persistence.History;
using CodexUsageMonitor.Persistence.Paths;
using CodexUsageMonitor.Windows.Runtime;

namespace CodexUsageMonitor.App.Runtime;

public sealed class RuntimeActionService
{
    private readonly UsageApplicationState _state;
    private readonly UsageHistoryRepository _history;
    private readonly SupportBundleBuilder _support;
    private readonly UsageDatabase _database;
    private readonly INativeNotificationService _native;
    private readonly EmailTransportFactory _email;
    private readonly ApplicationSettingsService _settings;
    private readonly AppDataPaths _paths;
    private readonly ProfileMonitoringCoordinatorService _profileMonitoring;
    private readonly StartupRegistrationReconciler _startupRegistration;
    private readonly Func<TrayIconManager> _trayFactory;
    private readonly LegacyMigrationActionService _legacyMigration;
    private readonly CodexUsageMonitor.Migration.Execution.LegacyMigrationRuntimeState _legacyMigrationState;
    private readonly RuntimeDiagnosticsService _diagnostics;
    private readonly UpdateCoordinatorService _updates;
    private readonly ApplicationLifetimeController _lifetime;
    private readonly IClock _clock;

    public RuntimeActionService(
        UsageApplicationState state,
        UsageHistoryRepository history,
        SupportBundleBuilder support,
        UsageDatabase database,
        INativeNotificationService native,
        EmailTransportFactory email,
        ApplicationSettingsService settings,
        AppDataPaths paths,
        ProfileMonitoringCoordinatorService profileMonitoring,
        StartupRegistrationReconciler startupRegistration,
        Func<TrayIconManager> trayFactory,
        LegacyMigrationActionService legacyMigration,
        CodexUsageMonitor.Migration.Execution.LegacyMigrationRuntimeState legacyMigrationState,
        RuntimeDiagnosticsService diagnostics,
        UpdateCoordinatorService updates,
        ApplicationLifetimeController lifetime,
        IClock clock)
    {
        _state = state;
        _history = history;
        _support = support;
        _database = database;
        _native = native;
        _email = email;
        _settings = settings;
        _paths = paths;
        _profileMonitoring = profileMonitoring;
        _startupRegistration = startupRegistration;
        _trayFactory = trayFactory;
        _legacyMigration = legacyMigration;
        _legacyMigrationState = legacyMigrationState;
        _diagnostics = diagnostics;
        _updates = updates;
        _lifetime = lifetime;
        _clock = clock;
    }

    public SettingsActions CreateSettingsActions() => new(
        LoadHistoryAsync,
        ExportSupportBundleAsync,
        _database.IntegrityCheckAsync,
        CaptureDiagnosticsAsync,
        TestNotificationAsync,
        TestEmailAsync,
        CheckForUpdatesAsync,
        PrepareUpdateAsync,
        ConfirmInstallUpdate,
        InstallUpdateAsync,
        () => _updates.Current,
        OpenLogsFolder,
        RestartConnectionAsync,
        ReconcileStartupAsync,
        ResetWidgetPositionAsync,
        () => _trayFactory().RefreshFromState(),
        _legacyMigration.GetSummaryAsync,
        _legacyMigration.RetireAsync,
        _legacyMigration.RestoreAsync,
        OpenMigrationBackupFolder);

    private async Task<IReadOnlyList<HistoryPoint>> LoadHistoryAsync(CancellationToken cancellationToken)
    {
        var snapshot = _state.ActiveSnapshot;
        if (snapshot is null)
        {
            return [];
        }

        var selection = LimitSelectionEngine.Select(
            snapshot.Limits,
            new LimitSelectionRequest(_settings.Current.Limits.SelectionMode, _settings.Current.Limits.ExplicitLimitIdentity, _settings.Current.Limits.PreferredModel, false));
        if (selection.Primary is null)
        {
            return [];
        }

        return await _history.ReadAsync(
            snapshot.ProfileId,
            snapshot.Account.StorageKey,
            selection.Primary.Identity,
            _clock.UtcNow.AddDays(-30),
            5_000,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ExportSupportBundleAsync(string destinationPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var snapshot = await _diagnostics.CaptureAsync(cancellationToken).ConfigureAwait(false);
        return await _support.BuildAsync(destinationPath, snapshot, _settings.Current, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> CaptureDiagnosticsAsync(CancellationToken cancellationToken) =>
        DiagnosticSummaryFormatter.Format(
            await _diagnostics.CaptureAsync(cancellationToken).ConfigureAwait(false));

    private Task TestNotificationAsync(CancellationToken cancellationToken) =>
        _native.ShowAsync(
            new NativeNotificationContent(
                "Codex Usage Monitor",
                "Notifications are configured correctly.",
                "Local test",
                _settings.Current.Notifications.PlaySound,
                [new NativeNotificationAction("Open settings", ActivationCommandNames.OpenSettings, SettingsSection.Notifications.ToString())]),
            cancellationToken);

    private async Task TestEmailAsync(CancellationToken cancellationToken)
    {
        var settings = _settings.Current.Email;
        if (settings.Provider is EmailProviderMode.Off || string.IsNullOrWhiteSpace(settings.ConnectedAddress ?? settings.SenderAddress))
        {
            throw new InvalidOperationException("Email is not fully configured.");
        }

        var message = new SelfNotification(
            "Codex Usage Monitor test",
            "This is a local configuration test from Codex Usage Monitor.",
            "<p>This is a local configuration test from <strong>Codex Usage Monitor</strong>.</p>",
            $"test:{Guid.NewGuid():N}");
        var transport = _email.ResolveForExplicitTest()
            ?? throw new InvalidOperationException("Email is not fully configured.");
        var result = await transport.SendSelfNotificationAsync(message, cancellationToken).ConfigureAwait(false);
        if (!result.Delivered)
        {
            throw new InvalidOperationException(result.SafeErrorCode ?? "email.test_failed");
        }
    }

    private Task<UpdateRuntimeSnapshot> CheckForUpdatesAsync(CancellationToken cancellationToken) =>
        _updates.CheckAsync(manual: true, cancellationToken);

    private Task<UpdateRuntimeSnapshot> PrepareUpdateAsync(CancellationToken cancellationToken) =>
        _updates.PrepareAsync(cancellationToken);

    private static bool ConfirmInstallUpdate() =>
        MessageBox.Show(
            System.Windows.Application.Current?.MainWindow,
            "Install the verified update and restart Codex Usage Monitor now? The application will close. Unsaved Settings changes will be discarded.",
            "Install and restart",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) is MessageBoxResult.Yes;

    private async Task<UpdateRuntimeSnapshot> InstallUpdateAsync(CancellationToken cancellationToken)
    {
        var result = await _updates.InstallPreparedAsync(cancellationToken).ConfigureAwait(false);
        if (result.Status is UpdateRuntimeStatus.Installing)
        {
            _lifetime.RequestExit();
        }

        return result;
    }

    private void OpenLogsFolder()
    {
        Directory.CreateDirectory(_paths.LogsDirectory);
        Process.Start(new ProcessStartInfo(_paths.LogsDirectory) { UseShellExecute = true });
    }

    private void OpenMigrationBackupFolder()
    {
        var archive = _legacyMigrationState.Migration?.BackupArchive;
        if (string.IsNullOrWhiteSpace(archive) || !File.Exists(archive))
        {
            throw new InvalidOperationException("A verified legacy migration backup is not available.");
        }

        var folder = Path.GetDirectoryName(Path.GetFullPath(archive))
            ?? throw new InvalidOperationException("The legacy backup folder is unavailable.");
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    private async Task<string> RestartConnectionAsync(CancellationToken cancellationToken)
    {
        var restarted = await _profileMonitoring.RestartAllAsync(cancellationToken).ConfigureAwait(false);
        return restarted == 0
            ? "No enabled background Codex profile is currently available to restart."
            : $"Restarted {restarted} Codex profile monitor{(restarted == 1 ? string.Empty : "s")}.";
    }

    private async Task<string> ReconcileStartupAsync(CancellationToken cancellationToken)
    {
        var result = await _startupRegistration.ReconcileNowAsync(cancellationToken).ConfigureAwait(false);
        return result.State switch
        {
            CodexUsageMonitor.Windows.Startup.StartupRegistrationState.Enabled => "Start with Windows is enabled and verified.",
            CodexUsageMonitor.Windows.Startup.StartupRegistrationState.Disabled => "Start with Windows is disabled and verified.",
            CodexUsageMonitor.Windows.Startup.StartupRegistrationState.EnabledByPolicy => "Start with Windows is enabled by Windows policy.",
            CodexUsageMonitor.Windows.Startup.StartupRegistrationState.DisabledByPolicy => "Start with Windows is disabled by Windows policy.",
            _ => $"Startup registration could not be reconciled safely ({result.SafeReasonCode ?? "startup.unavailable"}).",
        };
    }

    private async Task<string> ResetWidgetPositionAsync(CancellationToken cancellationToken)
    {
        await _settings.UpdateAsync(current => current with
        {
            Widget = current.Widget with { Placement = null },
        }, cancellationToken).ConfigureAwait(false);
        return "The saved widget position was reset. The widget will be clamped to the active monitor.";
    }

}
