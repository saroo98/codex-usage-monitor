using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using CodexUsageMonitor.Application.Diagnostics;
using CodexUsageMonitor.Application.Monitoring;
using CodexUsageMonitor.Application.Runtime;
using CodexUsageMonitor.Application.Settings;
using CodexUsageMonitor.App.ResetCredits;
using CodexUsageMonitor.App.Services;
using CodexUsageMonitor.App.ViewModels;
using CodexUsageMonitor.App.Views;
using CodexUsageMonitor.Codex;
using CodexUsageMonitor.Codex.Monitoring;
using CodexUsageMonitor.Codex.Transport;
using CodexUsageMonitor.Core.Abstractions;
using CodexUsageMonitor.Core.Diagnostics;
using CodexUsageMonitor.Core.Monitoring;
using CodexUsageMonitor.Core.Notifications;
using CodexUsageMonitor.Core.Scheduling;
using CodexUsageMonitor.Core.Security;
using CodexUsageMonitor.Email.OAuth;
using CodexUsageMonitor.Email.Outbox;
using CodexUsageMonitor.Migration.Discovery;
using CodexUsageMonitor.Migration.Execution;
using CodexUsageMonitor.Migration.Legacy;
using CodexUsageMonitor.Migration.Tasks;
using CodexUsageMonitor.Notifications.Delivery;
using CodexUsageMonitor.Notifications.Native;
using CodexUsageMonitor.Persistence.Database;
using CodexUsageMonitor.Persistence.Diagnostics;
using CodexUsageMonitor.Persistence.History;
using CodexUsageMonitor.Persistence.Notifications;
using CodexUsageMonitor.Persistence.Outbox;
using CodexUsageMonitor.Persistence.Paths;
using CodexUsageMonitor.Persistence.ResetCredits;
using CodexUsageMonitor.Persistence.Settings;
using CodexUsageMonitor.Updater.Install;
using CodexUsageMonitor.Updater.Manifest;
using CodexUsageMonitor.Updater.Model;
using CodexUsageMonitor.Updater.Network;
using CodexUsageMonitor.Updater.Security;
using CodexUsageMonitor.Updater.Staging;
using CodexUsageMonitor.Windows.Processes;
using CodexUsageMonitor.Windows.Runtime;
using CodexUsageMonitor.Windows.Security;
using CodexUsageMonitor.Windows.Startup;
using CodexUsageMonitor.Windows.SystemEvents;
using CodexUsageMonitor.Windows.Windowing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.App.Runtime;

public static class AppHostBuilder
{
    private const string ApplicationId = "CodexUsageMonitor.Windows";

    public static IHost Build(
        System.Windows.Application application,
        SynchronizationContext uiContext,
        AppHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(uiContext);
        options ??= new AppHostOptions();

        var paths = options.Paths ?? AppDataPathResolver.Resolve(AppContext.BaseDirectory);
        paths.EnsureCreated();

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = typeof(App).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            DisableDefaults = true,
        });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddProvider(new RedactingFileLoggerProvider(paths.LogsDirectory));

        var services = builder.Services;
        services.AddSingleton(application);
        services.AddSingleton(application.Dispatcher);
        services.AddSingleton(uiContext);
        services.AddSingleton(options);
        services.AddSingleton(paths);
        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddSingleton<IAsyncDelay>(SystemAsyncDelay.Instance);
        services.AddSingleton<IRandomSource>(RandomSource.Shared);
        services.AddSingleton<RetryBackoffPolicy>();
        services.AddSingleton(TimeZoneInfo.Local);

        services.AddSingleton<ISettingsStore>(provider =>
            options.SettingsStore ?? new JsonSettingsStore(paths.SettingsFile, provider.GetRequiredService<ILogger<JsonSettingsStore>>()));
        services.AddSingleton<ApplicationSettingsService>();
        services.AddSingleton<IApplicationSettingsSnapshot>(provider =>
            provider.GetRequiredService<ApplicationSettingsService>());
        services.AddSingleton(new SqliteConnectionFactory(paths.DatabaseFile));
        services.AddSingleton<UsageDatabase>();
        services.AddSingleton<UsageHistoryRepository>();
        services.AddSingleton<IUsageHistoryWriter>(provider =>
            provider.GetRequiredService<UsageHistoryRepository>());
        services.AddSingleton<NotificationReceiptRepository>();
        services.AddSingleton<DeferredNotificationRepository>();
        services.AddSingleton<EmailOutboxRepository>();
        services.AddSingleton<ResetRedemptionRepository>();
        services.AddSingleton<SupportBundleBuilder>();

        services.AddSingleton<ISecretStore, WindowsCredentialSecretStore>();
        services.AddSingleton<IProtectedDataStore, DpapiProtectedDataStore>();
        services.AddSingleton<OAuthTokenStore>();
        services.AddSingleton<ObsoleteEmailCredentialCleanup>();
        services.AddSingleton<IBrowserLauncher, SystemBrowserLauncher>();
        services.AddSingleton(options.EmailProviderRegistrations ?? EmailProviderRegistrations.FromAssembly(typeof(AppHostBuilder).Assembly));
        services.AddSingleton<MicrosoftPkceAuthorizationFlow>();
        services.AddSingleton<IMicrosoftPkceAuthorizationFlow>(provider => provider.GetRequiredService<MicrosoftPkceAuthorizationFlow>());
        services.AddSingleton<GooglePkceAuthorizationFlow>();
        services.AddSingleton<IGooglePkceAuthorizationFlow>(provider => provider.GetRequiredService<GooglePkceAuthorizationFlow>());
        services.AddSingleton<ProviderEmailAccountIdentityResolver>();
        services.AddSingleton<IProviderEmailAccountIdentityResolver>(provider => provider.GetRequiredService<ProviderEmailAccountIdentityResolver>());
        services.AddSingleton(provider => CreateHttpClient(options.HttpMessageHandler));

        services.AddSingleton<IProtocolAnomalySink, LoggingProtocolAnomalySink>();
        services.AddSingleton<CodexExecutableResolver>();
        Func<IProcessContainment> processContainmentFactory;
        if (options.ProcessContainmentFactory is { } configuredProcessContainmentFactory)
        {
            processContainmentFactory = configuredProcessContainmentFactory;
        }
        else if (options.TestMode)
        {
            processContainmentFactory = static () => NullProcessContainment.Instance;
        }
        else
        {
            processContainmentFactory = static () => new JobObjectProcessContainment();
        }

        services.AddSingleton(processContainmentFactory);
        services.AddSingleton<AppServerClientFactory>();
        services.AddSingleton<Func<ProfileMonitorSupervisor>>(provider =>
            () => ActivatorUtilities.CreateInstance<ProfileMonitorSupervisor>(provider));
        services.AddSingleton<MultiProfileMonitorCoordinator>();
        services.AddSingleton<IProfileMonitorLifecycle>(provider =>
            provider.GetRequiredService<MultiProfileMonitorCoordinator>());
        services.AddSingleton<ProfileMonitoringCoordinatorService>();

        services.AddSingleton<INativeNotificationService>(provider =>
            options.NativeNotificationService ??
            (options.TestMode
                ? new NoOpNativeNotificationService()
                : new WindowsAppNotificationService(provider.GetRequiredService<ILogger<WindowsAppNotificationService>>())));
        services.AddSingleton<DeferredNotificationSignal>();
        services.AddSingleton<NotificationDeliveryCoordinator>();
        services.AddSingleton<IUsageNotificationSink>(provider =>
            provider.GetRequiredService<NotificationDeliveryCoordinator>());

        services.AddSingleton<EmailOutboxPayloadCodec>();
        services.AddSingleton<EmailOutboxSignal>();
        services.AddSingleton<EmailRetryBackoffPolicy>();
        services.AddSingleton<EmailOutboxQueue>();
        services.AddSingleton<EmailTransportFactory>();
        services.AddSingleton<EmailCredentialService>();
        services.AddSingleton<OAuthConnectionService>();
        services.AddSingleton<EmailOutboxProcessor>(provider => new EmailOutboxProcessor(
            provider.GetRequiredService<EmailOutboxRepository>(),
            provider.GetRequiredService<EmailOutboxPayloadCodec>(),
            provider.GetRequiredService<EmailTransportFactory>().Resolve,
            provider.GetRequiredService<IClock>(),
            provider.GetRequiredService<EmailOutboxSignal>(),
            provider.GetRequiredService<EmailRetryBackoffPolicy>(),
            provider.GetRequiredService<ILogger<EmailOutboxProcessor>>()));
        services.AddSingleton<TransitionEmailDispatcher>();
        services.AddSingleton<IUsageEmailSink>(provider =>
            provider.GetRequiredService<TransitionEmailDispatcher>());

        services.AddSingleton<IApplicationEventDispatcher, SynchronizationContextEventDispatcher>();
        services.AddSingleton<IApplicationFailureSink, LoggingApplicationFailureSink>();
        services.AddSingleton<UsageApplicationState>();
        services.AddSingleton<IUsageRuntimeSnapshotProvider>(provider =>
            provider.GetRequiredService<UsageApplicationState>());
        services.AddSingleton<IProfileMonitorCallbacks>(provider =>
            provider.GetRequiredService<UsageApplicationState>());
        services.AddSingleton<ResetCreditRedemptionService>();

        services.AddSingleton<LegacyInstallationDiscovery>();
        services.AddSingleton<LegacyJsonReader>();
        services.AddSingleton<LegacySettingsMapper>();
        services.AddSingleton<LegacyBackupService>();
        services.AddSingleton<ILegacyTaskCommandRunner, SchtasksCommandRunner>();
        services.AddSingleton<ILegacyScheduledTaskController, LegacyScheduledTaskController>();
        services.AddSingleton<LegacyMigrationCoordinator>();
        services.AddSingleton<LegacyTaskRetirementCoordinator>();
        services.AddSingleton<ILegacyTaskRetirementCoordinator>(provider =>
            provider.GetRequiredService<LegacyTaskRetirementCoordinator>());
        services.AddSingleton<LegacyMigrationRuntimeState>();
        services.AddSingleton<ILegacyMigrationStatePort, LegacyMigrationStateAdapter>();
        services.AddSingleton<ILegacyBackupVerificationPort, LegacyBackupVerificationAdapter>();
        services.AddSingleton<LegacyMigrationActionService>();
        services.AddSingleton<RuntimeDiagnosticsService>();

        services.AddSingleton<IExecutableSignatureVerifier, AuthenticodeSignatureVerifier>();
        services.AddSingleton(new UpdateTrustPolicyOptions(options.AllowUnsignedDevelopmentUpdates));
        services.AddSingleton<UpdateArtifactTrustPolicy>();
        services.AddSingleton<SafeZipExtractor>();
        services.AddSingleton<PortableUpdateStager>();
        services.AddSingleton<UpdateAssetDownloader>();
        services.AddSingleton<PortableUpdateTransaction>();
        services.AddSingleton<PortableUpdateRecovery>();
        services.AddSingleton<IUpdaterHostFileCopier, UpdaterHostFileCopier>();
        services.AddSingleton<IUpdaterHostStarter, ProcessUpdaterHostStarter>();
        services.AddSingleton<PortableUpdateLauncher>();
        var productVersion = typeof(App).Assembly.GetName().Version
            ?? throw new InvalidOperationException("The application assembly version is unavailable.");
        services.AddSingleton(
            typeof(SemanticVersion),
            new SemanticVersion(productVersion.Major, productVersion.Minor, productVersion.Build));
        services.AddSingleton<ManifestSignatureVerifier>(_ => new ManifestSignatureVerifier(ReleaseTrustAnchor.Load()));
        services.AddSingleton<UpdateManifestValidator>();
        services.AddSingleton<UpdateManifestClient>();
        services.AddSingleton<CodexUsageMonitor.Updater.UpdateCheckService>();
        services.AddSingleton(provider => new UpdateRuntimeState(
            provider.GetRequiredService<SemanticVersion>().ToString()));
        services.AddSingleton<UpdateCheckSignal>();
        services.AddSingleton<IUpdatePlatformPort, UpdatePlatformAdapter>();
        services.AddSingleton<UpdateCoordinatorService>();
        services.AddSingleton<UpdateInstallOnExitCoordinator>();

        services.AddSingleton<IApplicationPackageContext, WindowsApplicationPackageContext>();
        services.AddSingleton<IStartupRegistration>(_ => options.StartupRegistration ??
            StartupRegistrationFactory.Create(Environment.ProcessPath ?? throw new InvalidOperationException("The application executable path is unavailable.")));
        services.AddSingleton<WindowsSystemEventSource>();
        services.AddSingleton<MonitorPlacementService>();
        if (options.SingleInstanceCoordinator is not null)
        {
            services.AddSingleton(options.SingleInstanceCoordinator);
        }
        else
        {
            services.AddSingleton<SingleInstanceCoordinator>(provider =>
                new SingleInstanceCoordinator(ApplicationId, provider.GetRequiredService<ILogger<SingleInstanceCoordinator>>()));
        }

        services.AddSingleton<ThemeManager>();
        services.AddSingleton<ApplicationStartupState>();
        services.AddSingleton<ApplicationReadinessGate>();
        services.AddSingleton<IApplicationProcessIdentity, SystemApplicationProcessIdentity>();
        services.AddSingleton<ApplicationLifetimeController>();
        services.AddSingleton<StartupRegistrationReconciler>();
        services.AddSingleton<StartupHealthMarkerWriter>();
        services.AddSingleton<UsageStartupHealthClassificationSource>();
        services.AddSingleton<IStartupHealthClassificationSource>(provider =>
            provider.GetRequiredService<UsageStartupHealthClassificationSource>());
        services.AddSingleton<StartupHealthQualification>();
        services.AddSingleton<UpdateRecoveryCoordinator>();
        services.AddSingleton<DeferredNotificationResolver>();
        services.AddSingleton<UiActionDispatcher>();
        services.AddSingleton<Func<SettingsWindow>>(provider => () => provider.GetRequiredService<SettingsWindow>());
        services.AddSingleton<Func<OnboardingWindow>>(provider => () => provider.GetRequiredService<OnboardingWindow>());
        services.AddSingleton<Func<ResetRedemptionIntent, ResetCreditConfirmationDialog>>(_ =>
            intent => new ResetCreditConfirmationDialog(intent));
        services.AddSingleton<Func<IWidgetWindow>>(provider => () => provider.GetRequiredService<WidgetWindow>());
        services.AddSingleton<WidgetWindowSession>();
        services.AddSingleton<WindowCoordinator>();
        services.AddSingleton<Func<WindowCoordinator>>(provider => () => provider.GetRequiredService<WindowCoordinator>());
        services.AddSingleton<ApplicationCommandRouter>();
        services.AddSingleton<SystemEventCoordinator>();
        services.AddSingleton<Func<SystemEventCoordinator>>(provider => () => provider.GetRequiredService<SystemEventCoordinator>());
        services.AddSingleton<NativeActivationCoordinator>();
        services.AddSingleton<Func<NativeActivationCoordinator>>(provider => () => provider.GetRequiredService<NativeActivationCoordinator>());
        services.AddSingleton<RuntimeActionService>();
        services.AddSingleton<WidgetActions>(provider => provider.GetRequiredService<UiActionDispatcher>().CreateWidgetActions());
        services.AddSingleton<WidgetViewModel>();
        services.AddTransient<WidgetWindow>();
        services.AddTransient<SettingsActions>(provider => provider.GetRequiredService<RuntimeActionService>().CreateSettingsActions());
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SettingsWindow>();
        services.AddTransient<OnboardingWindow>();
        services.AddSingleton<TrayActions>(provider => provider.GetRequiredService<UiActionDispatcher>().CreateTrayActions());
        services.AddSingleton<TrayIconManager>();
        services.AddSingleton<Func<TrayIconManager>>(provider => () => provider.GetRequiredService<TrayIconManager>());
        services.AddSingleton<ApplicationBootstrapper>();

        services.AddHostedService<EmailOutboxBackgroundService>();
        services.AddHostedService<DeferredNotificationBackgroundService>();
        services.AddHostedService<DatabaseMaintenanceBackgroundService>();
        services.AddHostedService<UpdateCheckBackgroundService>();

        return builder.Build();
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler? handler)
    {
        handler ??= new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 4,
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(45),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
    }
}
