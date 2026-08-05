using System.Threading.Channels;
using System.Windows;
using System.Windows.Threading;
using CodexUsageMonitor.App.Runtime;
using CodexUsageMonitor.Updater.Staging;
using CodexUsageMonitor.Windows.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexUsageMonitor.App;

public partial class App : System.Windows.Application
{
    private const string ApplicationId = "CodexUsageMonitor.Windows";
    private readonly CancellationTokenSource _applicationCancellation = new();
    private readonly Channel<ActivationMessage> _activationInbox = Channel.CreateBounded<ActivationMessage>(
        new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    private SingleInstanceCoordinator? _singleInstance;
    private IHost? _host;
    private ApplicationBootstrapper? _bootstrapper;
    private ApplicationStartupState? _startupState;
    private UpdateRecoveryCoordinator? _updateRecovery;
    private Task? _activationPump;
    private int _shutdownStarted;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var launch = AppCommandLine.Parse(e.Args);
        if (!launch.IsValid)
        {
            ShowSafeStartupError(launch.SafeErrorCode ?? "command_line.invalid");
            Shutdown(launch.ExitCode);
            return;
        }

        try
        {
            _singleInstance = new SingleInstanceCoordinator(
                ApplicationId,
                NullLogger<SingleInstanceCoordinator>.Instance);
            if (!_singleInstance.TryAcquirePrimary())
            {
                var accepted = await _singleInstance.ForwardAsync(
                    launch.ToActivationMessage(),
                    TimeSpan.FromSeconds(5),
                    _applicationCancellation.Token);
                await _singleInstance.DisposeAsync();
                _singleInstance = null;
                Shutdown(accepted ? 0 : 3);
                return;
            }

            _singleInstance.StartListening(QueueActivationAsync);
            var uiContext = SynchronizationContext.Current ?? new DispatcherSynchronizationContext(Dispatcher);
            _host = AppHostBuilder.Build(
                this,
                uiContext,
                new AppHostOptions
                {
                    SingleInstanceCoordinator = _singleInstance,
                    AllowUnsignedDevelopmentUpdates = UpdateTrustPolicyOptions.FromEnvironment().AllowUnsignedDevelopmentArtifacts,
                });
            var host = _host;
            var packageContext = host.Services.GetRequiredService<CodexUsageMonitor.Windows.Startup.IApplicationPackageContext>();
            if (packageContext.IsPackaged && launch.HasPortableUpdateCommand)
            {
                ShowSafeStartupError("update.portable_entry_point_rejected");
                Shutdown(2);
                return;
            }

            _startupState = host.Services.GetRequiredService<ApplicationStartupState>();
            _startupState.Begin();
            _startupState.Advance(ApplicationStartupStage.HostConstruction);

            await host.StartAsync(_applicationCancellation.Token);
            _updateRecovery = host.Services.GetRequiredService<UpdateRecoveryCoordinator>();
            _startupState.Advance(ApplicationStartupStage.UpdateRecovery);
            if (await _updateRecovery.InspectAsync(_applicationCancellation.Token))
            {
                return;
            }

            var lifetime = host.Services.GetRequiredService<ApplicationLifetimeController>();
            lifetime.UpdateRolledBack += OnUpdateRolledBack;

            var router = host.Services.GetRequiredService<ApplicationCommandRouter>();
            _activationPump = PumpActivationsAsync(router, _applicationCancellation.Token);
            _bootstrapper = host.Services.GetRequiredService<ApplicationBootstrapper>();
            var normalizedLaunch = await _bootstrapper.StartAsync(launch, _applicationCancellation.Token);
            await router.RouteAsync(normalizedLaunch.ToActivationMessage(), _applicationCancellation.Token);

            _startupState.Advance(ApplicationStartupStage.Ready);
            host.Services.GetRequiredService<ApplicationReadinessGate>().SignalReady();
            await router.SetReadyAsync(_applicationCancellation.Token);
            if (await _updateRecovery.CompleteHealthyStartupAsync(_applicationCancellation.Token))
            {
                return;
            }

            void OnUpdateRolledBack(object? sender, Guid transactionId) =>
                host.Services.GetRequiredService<ILogger<App>>()
                    .LogWarning("The previous update transaction {TransactionId} was rolled back.", transactionId);
        }
        catch (OperationCanceledException) when (_applicationCancellation.IsCancellationRequested)
        {
            Shutdown(0);
        }
        catch (Exception exception)
        {
            _startupState?.Fail("startup.unhandled_failure");
            LogCritical(exception, "Application startup failed.");
            await TryScheduleUpdateRollbackAfterStartupFailureAsync();
            ShowSafeStartupError("startup.unhandled_failure");
            Shutdown(1);
        }
    }

    private async Task TryScheduleUpdateRollbackAfterStartupFailureAsync()
    {
        try
        {
            _updateRecovery ??= _host?.Services.GetService<UpdateRecoveryCoordinator>();
            if (_updateRecovery is null)
            {
                return;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _updateRecovery.HandleStartupFailureAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            LogCritical(
                new TimeoutException("Update rollback scheduling exceeded the startup-failure timeout."),
                "Interrupted update recovery timed out after application startup failed.");
        }
        catch (Exception recoveryException) when (recoveryException is
            IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            InvalidOperationException or
            System.Security.Cryptography.CryptographicException or
            System.ComponentModel.Win32Exception)
        {
            LogCritical(recoveryException, "Interrupted update recovery failed after application startup failed.");
        }
    }

    private async Task QueueActivationAsync(ActivationMessage message, CancellationToken cancellationToken)
    {
        if (!message.TryValidate(out _))
        {
            return;
        }

        await _activationInbox.Writer.WriteAsync(message, cancellationToken);
    }

    private async Task PumpActivationsAsync(ApplicationCommandRouter router, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in _activationInbox.Reader.ReadAllAsync(cancellationToken))
            {
                await router.RouteAsync(message, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            LogCritical(exception, "The local activation pump terminated unexpectedly.");
            _ = Dispatcher.BeginInvoke(() => Shutdown(4));
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            ShutdownCoreAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            LogCritical(exception, "Application shutdown did not complete cleanly.");
        }
        finally
        {
            DispatcherUnhandledException -= OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            _applicationCancellation.Dispose();
            base.OnExit(e);
        }
    }

    private async Task ShutdownCoreAsync()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            return;
        }

        _startupState?.Advance(ApplicationStartupStage.Stopping);
        _activationInbox.Writer.TryComplete();
        await _applicationCancellation.CancelAsync();

        if (_activationPump is not null)
        {
            try
            {
                await _activationPump.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
            {
            }
        }

        if (_bootstrapper is not null)
        {
            await _bootstrapper.DisposeAsync();
        }

        if (_host is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await _host.StopAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
            }

            if (_host is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else
            {
                _host.Dispose();
            }

            _host = null;
            _singleInstance = null;
        }
        else if (_singleInstance is not null)
        {
            await _singleInstance.DisposeAsync();
            _singleInstance = null;
        }

        _startupState?.Advance(ApplicationStartupStage.Stopped);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs eventArgs)
    {
        LogCritical(eventArgs.Exception, "Unhandled dispatcher exception.");
        eventArgs.Handled = true;
        Shutdown(5);
    }

    private void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs eventArgs)
    {
        if (eventArgs.ExceptionObject is Exception exception)
        {
            LogCritical(exception, "Unhandled application-domain exception.");
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs eventArgs)
    {
        LogCritical(eventArgs.Exception, "Unobserved task exception.");
        eventArgs.SetObserved();
    }

    private void LogCritical(Exception exception, string message)
    {
        try
        {
            _host?.Services.GetService<ILogger<App>>()?.LogCritical(exception, "{Message}", message);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void ShowSafeStartupError(string code) =>
        MessageBox.Show(
            $"Codex Usage Monitor could not start safely ({code}). Check the application log for details.",
            "Codex Usage Monitor",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

}
