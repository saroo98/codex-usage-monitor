using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using CodexUsageMonitor.App.Services;
using CodexUsageMonitor.Codex.Monitoring;
using CodexUsageMonitor.Core.Settings;
using CodexUsageMonitor.Migration.Execution;
using CodexUsageMonitor.Notifications.Native;
using CodexUsageMonitor.Persistence.Database;
using CodexUsageMonitor.Windows.Runtime;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.App.Runtime;

public sealed class ApplicationBootstrapper : IAsyncDisposable
{
    private readonly System.Windows.Application _application;
    private readonly UsageDatabase _database;
    private readonly LegacyMigrationCoordinator _migration;
    private readonly ILegacyTaskRetirementCoordinator _legacyTaskRetirement;
    private readonly LegacyMigrationRuntimeState _legacyMigrationState;
    private readonly ApplicationSettingsService _settings;
    private readonly ThemeManager _themes;
    private readonly INativeNotificationService _nativeNotifications;
    private readonly Func<NativeActivationCoordinator> _nativeActivationFactory;
    private readonly StartupRegistrationReconciler _startupRegistration;
    private readonly Func<SystemEventCoordinator> _systemEventsFactory;
    private readonly ProfileMonitoringCoordinatorService _profileMonitoring;
    private readonly UsageApplicationState _usageState;
    private readonly Func<WindowCoordinator> _windowsFactory;
    private readonly Func<TrayIconManager> _trayFactory;
    private readonly UiActionDispatcher _uiActions;
    private NativeActivationCoordinator? _nativeActivation;
    private SystemEventCoordinator? _systemEvents;
    private WindowCoordinator? _windows;
    private TrayIconManager? _tray;
    private readonly ApplicationStartupState _startup;
    private readonly ApplicationLifetimeController _lifetime;
    private readonly UpdateInstallOnExitCoordinator _updateInstallOnExit;
    private readonly ILogger<ApplicationBootstrapper> _logger;
    private readonly SemaphoreSlim _settingsReconciliationGate = new(1, 1);
    private bool _started;
    private bool _disposed;

    public ApplicationBootstrapper(
        System.Windows.Application application,
        UsageDatabase database,
        LegacyMigrationCoordinator migration,
        ILegacyTaskRetirementCoordinator legacyTaskRetirement,
        LegacyMigrationRuntimeState legacyMigrationState,
        ApplicationSettingsService settings,
        ThemeManager themes,
        INativeNotificationService nativeNotifications,
        Func<NativeActivationCoordinator> nativeActivationFactory,
        StartupRegistrationReconciler startupRegistration,
        Func<SystemEventCoordinator> systemEventsFactory,
        ProfileMonitoringCoordinatorService profileMonitoring,
        UsageApplicationState usageState,
        Func<WindowCoordinator> windowsFactory,
        Func<TrayIconManager> trayFactory,
        UiActionDispatcher uiActions,
        ApplicationStartupState startup,
        ApplicationLifetimeController lifetime,
        UpdateInstallOnExitCoordinator updateInstallOnExit,
        ILogger<ApplicationBootstrapper> logger)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _migration = migration ?? throw new ArgumentNullException(nameof(migration));
        _legacyTaskRetirement = legacyTaskRetirement ?? throw new ArgumentNullException(nameof(legacyTaskRetirement));
        _legacyMigrationState = legacyMigrationState ?? throw new ArgumentNullException(nameof(legacyMigrationState));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _themes = themes ?? throw new ArgumentNullException(nameof(themes));
        _nativeNotifications = nativeNotifications ?? throw new ArgumentNullException(nameof(nativeNotifications));
        _nativeActivationFactory = nativeActivationFactory ?? throw new ArgumentNullException(nameof(nativeActivationFactory));
        _startupRegistration = startupRegistration ?? throw new ArgumentNullException(nameof(startupRegistration));
        _systemEventsFactory = systemEventsFactory ?? throw new ArgumentNullException(nameof(systemEventsFactory));
        _profileMonitoring = profileMonitoring ?? throw new ArgumentNullException(nameof(profileMonitoring));
        _usageState = usageState ?? throw new ArgumentNullException(nameof(usageState));
        _windowsFactory = windowsFactory ?? throw new ArgumentNullException(nameof(windowsFactory));
        _trayFactory = trayFactory ?? throw new ArgumentNullException(nameof(trayFactory));
        _uiActions = uiActions ?? throw new ArgumentNullException(nameof(uiActions));
        _startup = startup ?? throw new ArgumentNullException(nameof(startup));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _updateInstallOnExit = updateInstallOnExit ?? throw new ArgumentNullException(nameof(updateInstallOnExit));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<AppLaunchRequest> StartAsync(AppLaunchRequest launch, CancellationToken cancellationToken)
    {
        if (!launch.IsValid)
        {
            throw new ArgumentException("A valid launch request is required.", nameof(launch));
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            throw new InvalidOperationException("Application bootstrap has already completed.");
        }

        _started = true;
        _startup.Advance(ApplicationStartupStage.DataInitialization);
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);

        await ImportLegacyStateAsync(cancellationToken).ConfigureAwait(false);

        _startup.Advance(ApplicationStartupStage.SettingsInitialization);
        var settingsResult = await _settings.InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (!settingsResult.CanPersist)
        {
            _startup.AddDegraded(
                "settings",
                settingsResult.Issues.Count > 0 ? settingsResult.Issues[0].Code : "settings.read_only");
        }

        _settings.Changed += OnSettingsChanged;
        _lifetime.RegisterExitPreparation(_updateInstallOnExit.PrepareExitAsync);

        _startup.Advance(ApplicationStartupStage.ThemeInitialization);
        await _application.Dispatcher.InvokeAsync(
            () => _themes.Apply(_settings.Current.Widget.Theme),
            DispatcherPriority.Send,
            cancellationToken).Task.ConfigureAwait(false);

        _startup.Advance(ApplicationStartupStage.NativeNotificationRegistration);
        try
        {
            _nativeNotifications.Register();
            _nativeActivation = _nativeActivationFactory();
            _nativeActivation.Start();
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or COMException)
        {
            _startup.AddDegraded("native-notifications", "notifications.registration_failed");
            _logger.LogWarning(exception, "Native notification registration failed; monitoring remains available.");
        }

        _startup.Advance(ApplicationStartupStage.StartupRegistration);
        try
        {
            _startupRegistration.Start(_lifetime.ApplicationToken);
            var startup = await _startupRegistration.ReconcileNowAsync(cancellationToken).ConfigureAwait(false);
            if (startup.IsEnabled != _settings.Current.General.StartWithWindows)
            {
                _startup.AddDegraded("startup-registration", startup.SafeReasonCode ?? "startup.reconciliation_failed");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            _startup.AddDegraded("startup-registration", "startup.reconciliation_failed");
            _logger.LogWarning(exception, "Startup registration reconciliation failed.");
        }

        _startup.Advance(ApplicationStartupStage.SystemEvents);
        try
        {
            _systemEvents = _systemEventsFactory();
            _systemEvents.Start();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ExternalException)
        {
            _startup.AddDegraded("system-events", "system_events.registration_failed");
            _logger.LogWarning(exception, "Windows system-event observation could not be started.");
        }

        _startup.Advance(ApplicationStartupStage.BackgroundServices);
        _startup.Advance(ApplicationStartupStage.Monitoring);
        _profileMonitoring.Start(_lifetime.ApplicationToken);
        EnsureActiveProfile(_settings.Current);

        _startup.Advance(ApplicationStartupStage.UserInterface);
        await _application.Dispatcher.InvokeAsync(
            () => InitializeUserInterface(launch.Background),
            DispatcherPriority.Send,
            cancellationToken).Task.ConfigureAwait(false);

        return NormalizeLaunch(launch, _settings.Current);
    }

    private async Task ImportLegacyStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _migration.ExecuteAsync(
                new LegacyMigrationOptions(RemoveLegacyScheduledTasks: false, TaskRemovalExplicitlyConfirmed: false),
                cancellationToken).ConfigureAwait(false);
            _legacyMigrationState.SetMigration(result);
            _legacyMigrationState.SetRetirement(
                await _legacyTaskRetirement.GetStateAsync(cancellationToken).ConfigureAwait(false));
            if (result.SafeErrorCode is not null)
            {
                _startup.AddDegraded("legacy-migration", result.SafeErrorCode);
            }
            else if (result.Migrated)
            {
                _logger.LogInformation(
                    "Imported legacy Codex Usage Notifier settings from version {LegacyVersion}; legacy tasks remain unchanged until explicit retirement.",
                    result.LegacyVersion ?? "unknown");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            _startup.AddDegraded("legacy-migration", "migration.failed");
            _logger.LogWarning(exception, "Legacy migration was skipped after a recoverable failure.");
        }
    }

    private void InitializeUserInterface(bool backgroundLaunch)
    {
        var windows = _windowsFactory();
        _windows = windows;
        _uiActions.Attach(windows);
        var tray = _trayFactory();
        _tray = tray;
        tray.SetWidgetVisible(false);
        var settings = _settings.Current;
        if (!backgroundLaunch && settings.General.ShowOnboardingOnNextLaunch)
        {
            var accepted = windows.ShowOnboarding();
            if (accepted && _settings.CanPersist)
            {
                _ = PersistOnboardingCompletionAsync(_lifetime.ApplicationToken);
            }
        }
    }

    private async Task PersistOnboardingCompletionAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _settings.UpdateAsync(current => current with
            {
                General = current.General with { ShowOnboardingOnNextLaunch = false },
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Onboarding completion could not be persisted.");
        }
    }

    private static AppLaunchRequest NormalizeLaunch(AppLaunchRequest launch, AppSettings settings)
    {
        if (!launch.ApplyLaunchMinimizedPreference || launch.Background || !settings.General.LaunchMinimized ||
            launch.Commands.Count != 1 || launch.Commands[0].Name != ActivationCommandNames.ShowWidget)
        {
            return launch;
        }

        return launch with
        {
            Commands = [new ActivationCommand(ActivationCommandNames.HideWidget)],
        };
    }

    private void EnsureActiveProfile(AppSettings settings)
    {
        if (_usageState.ActiveProfileId is null ||
            !settings.Profiles.Any(profile => profile.Enabled && profile.Id == _usageState.ActiveProfileId))
        {
            _usageState.SetActiveProfile(settings.Profiles.FirstOrDefault(static profile => profile.Enabled)?.Id);
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        _ = ReconcileSettingsAsync(settings, _lifetime.ApplicationToken);
    }

    private async Task ReconcileSettingsAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            await _settingsReconciliationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureActiveProfile(settings);
                await _application.Dispatcher.InvokeAsync(
                    () => _themes.Apply(settings.Widget.Theme),
                    DispatcherPriority.Background,
                    cancellationToken).Task.ConfigureAwait(false);
                _startupRegistration.RequestReconcile();
            }
            finally
            {
                _settingsReconciliationGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning(exception, "A runtime settings change could not be fully reconciled.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        void CloseUi()
        {
            _windows?.CloseForExit();
            _tray?.Dispose();
        }

        if (_application.Dispatcher.CheckAccess())
        {
            CloseUi();
        }
        else if (!_application.Dispatcher.HasShutdownStarted && !_application.Dispatcher.HasShutdownFinished)
        {
            await _application.Dispatcher.InvokeAsync(CloseUi, DispatcherPriority.Send).Task.ConfigureAwait(false);
        }

        _nativeActivation?.Dispose();
        _systemEvents?.Dispose();
        await _profileMonitoring.DisposeAsync().ConfigureAwait(false);
        await _startupRegistration.DisposeAsync().ConfigureAwait(false);
        _settingsReconciliationGate.Dispose();
    }
}
