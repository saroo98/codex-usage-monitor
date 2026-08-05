using System.Threading.Channels;
using System.Windows.Threading;
using CodexUsageMonitor.App.Services;
using CodexUsageMonitor.App.ViewModels;
using CodexUsageMonitor.Codex.Monitoring;
using CodexUsageMonitor.Windows.Runtime;
using CodexUsageMonitor.Windows.Startup;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.App.Runtime;

public sealed class ApplicationCommandRouter
{
    private const int MaximumPendingMessages = 32;
    private readonly Dispatcher _dispatcher;
    private readonly WindowCoordinator _windows;
    private readonly UsageApplicationState _state;
    private readonly MultiProfileMonitorCoordinator _monitors;
    private readonly ApplicationLifetimeController _lifetime;
    private readonly StartupHealthMarkerWriter _healthWriter;
    private readonly IApplicationPackageContext _packageContext;
    private readonly ILogger<ApplicationCommandRouter> _logger;
    private readonly Channel<ActivationMessage> _pending = Channel.CreateBounded<ActivationMessage>(
        new BoundedChannelOptions(MaximumPendingMessages)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    private int _ready;

    public ApplicationCommandRouter(
        Dispatcher dispatcher,
        WindowCoordinator windows,
        UsageApplicationState state,
        MultiProfileMonitorCoordinator monitors,
        ApplicationLifetimeController lifetime,
        StartupHealthMarkerWriter healthWriter,
        IApplicationPackageContext packageContext,
        ILogger<ApplicationCommandRouter> logger)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _monitors = monitors ?? throw new ArgumentNullException(nameof(monitors));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _healthWriter = healthWriter ?? throw new ArgumentNullException(nameof(healthWriter));
        _packageContext = packageContext ?? throw new ArgumentNullException(nameof(packageContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> RouteAsync(ActivationMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!message.TryValidate(out var safeErrorCode))
        {
            _logger.LogWarning("Rejected activation message with code {SafeErrorCode}.", safeErrorCode);
            return false;
        }

        if (Volatile.Read(ref _ready) == 0)
        {
            await _pending.Writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
            return true;
        }

        return await DispatchAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetReadyAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _ready, 1) != 0)
        {
            return;
        }

        _pending.Writer.TryComplete();
        await foreach (var message in _pending.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await DispatchAsync(message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> DispatchAsync(ActivationMessage message, CancellationToken cancellationToken)
    {
        foreach (var command in message.Commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await RouteCommandAsync(command, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<bool> RouteCommandAsync(ActivationCommand command, CancellationToken cancellationToken)
    {
        switch (command.Name)
        {
            case ActivationCommandNames.ShowWidget:
                await InvokeAsync(_windows.ShowWidget, cancellationToken).ConfigureAwait(false);
                return true;
            case ActivationCommandNames.HideWidget:
                await InvokeAsync(_windows.HideWidget, cancellationToken).ConfigureAwait(false);
                return true;
            case ActivationCommandNames.OpenSettings:
                var section = Enum.TryParse<SettingsSection>(command.Value, ignoreCase: true, out var parsedSection)
                    ? parsedSection
                    : SettingsSection.General;
                await InvokeAsync(() => _windows.OpenSettings(section), cancellationToken).ConfigureAwait(false);
                return true;
            case ActivationCommandNames.OpenDiagnostics:
                await InvokeAsync(() => _windows.OpenSettings(SettingsSection.Diagnostics), cancellationToken).ConfigureAwait(false);
                return true;
            case ActivationCommandNames.Refresh:
                if (_state.ActiveProfileId is { } activeProfileId)
                {
                    _monitors.RequestRefresh(activeProfileId);
                }
                else
                {
                    _monitors.RequestRefreshAll();
                }

                return true;
            case ActivationCommandNames.DisableClickThrough:
                await _dispatcher.InvokeAsync(
                    () => _windows.DisableClickThroughAsync(cancellationToken),
                    DispatcherPriority.Send,
                    cancellationToken).Task.Unwrap().ConfigureAwait(false);
                await InvokeAsync(_windows.ShowWidget, cancellationToken).ConfigureAwait(false);
                return true;
            case ActivationCommandNames.ReviewResetCredit when Guid.TryParse(command.Value, out var profileId) && profileId != Guid.Empty:
                await _dispatcher.InvokeAsync(
                    () => _windows.ReviewResetCreditAsync(profileId, cancellationToken),
                    DispatcherPriority.Send,
                    cancellationToken).Task.Unwrap().ConfigureAwait(false);
                return true;
            case ActivationCommandNames.UpdateHealth when StartupHealthRequest.TryDecode(command.Value, out var healthRequest):
                if (_packageContext.IsPackaged)
                {
                    _logger.LogWarning("Rejected portable update health activation under package identity.");
                    return false;
                }

                return await _healthWriter.WriteAsync(healthRequest, cancellationToken).ConfigureAwait(false);
            case ActivationCommandNames.UpdateRolledBack when Guid.TryParse(command.Value, out var transactionId) && transactionId != Guid.Empty:
                if (_packageContext.IsPackaged)
                {
                    _logger.LogWarning("Rejected portable rollback activation under package identity.");
                    return false;
                }

                _lifetime.NotifyUpdateRolledBack(transactionId);
                await InvokeAsync(_windows.ShowWidget, cancellationToken).ConfigureAwait(false);
                return true;
            case ActivationCommandNames.Exit:
                _lifetime.RequestExit();
                return true;
            default:
                _logger.LogWarning("Rejected unsupported activation command {CommandName}.", command.Name);
                return false;
        }
    }

    private async Task InvokeAsync(Action action, CancellationToken cancellationToken)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        await _dispatcher.InvokeAsync(action, DispatcherPriority.Send, cancellationToken).Task.ConfigureAwait(false);
    }
}
