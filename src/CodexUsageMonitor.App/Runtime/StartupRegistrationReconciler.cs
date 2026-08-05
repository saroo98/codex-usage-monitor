using System.Threading.Channels;
using CodexUsageMonitor.App.Services;
using CodexUsageMonitor.Core.Settings;
using CodexUsageMonitor.Windows.Startup;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.App.Runtime;

public sealed class StartupRegistrationReconciler : IAsyncDisposable
{
    private readonly ApplicationSettingsService _settings;
    private readonly IStartupRegistration _registration;
    private readonly ILogger<StartupRegistrationReconciler> _logger;
    private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
    });
    private CancellationTokenSource? _lifetime;
    private Task? _worker;
    private int _updatingSettings;

    public StartupRegistrationReconciler(
        ApplicationSettingsService settings,
        IStartupRegistration registration,
        ILogger<StartupRegistrationReconciler> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public StartupRegistrationResult? LastResult { get; private set; }

    public event EventHandler<StartupRegistrationResult>? StateChanged;

    public void Start(CancellationToken applicationToken)
    {
        if (_worker is not null)
        {
            return;
        }

        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(applicationToken);
        _settings.Changed += OnSettingsChanged;
        _worker = RunAsync(_lifetime.Token);
        RequestReconcile();
    }

    public void RequestReconcile() => _requests.Writer.TryWrite(true);

    public async Task<StartupRegistrationResult> ReconcileNowAsync(CancellationToken cancellationToken)
    {
        var desired = _settings.Current.General.StartWithWindows;
        var current = await _registration.GetStateAsync(cancellationToken).ConfigureAwait(false);
        var result = current;
        if (current.CanChange && current.IsEnabled != desired)
        {
            result = await _registration.SetEnabledAsync(desired, cancellationToken).ConfigureAwait(false);
        }

        LastResult = result;
        StateChanged?.Invoke(this, result);
        if (result.CanChange && result.IsEnabled != desired)
        {
            _logger.LogWarning(
                "Startup registration did not reach the requested state. Desired={Desired}, Actual={Actual}, Code={SafeCode}.",
                desired,
                result.State,
                result.SafeReasonCode);
        }
        else if (!result.CanChange && result.IsEnabled != desired)
        {
            _logger.LogInformation(
                "Startup registration is controlled externally. Desired={Desired}, Actual={Actual}, Code={SafeCode}.",
                desired,
                result.State,
                result.SafeReasonCode);
        }

        if (_settings.CanPersist && Interlocked.CompareExchange(ref _updatingSettings, 1, 0) == 0)
        {
            try
            {
                if ((result.State is StartupRegistrationState.EnabledByPolicy or StartupRegistrationState.DisabledByPolicy) &&
                    _settings.Current.General.StartWithWindows != result.IsEnabled)
                {
                    await _settings.UpdateAsync(settings => settings with
                    {
                        General = settings.General with { StartWithWindows = result.IsEnabled },
                    }, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                Volatile.Write(ref _updatingSettings, 0);
            }
        }

        return result;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var _ in _requests.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await ReconcileNowAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    _logger.LogWarning(exception, "Startup registration reconciliation failed.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        if (Volatile.Read(ref _updatingSettings) == 0)
        {
            RequestReconcile();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _settings.Changed -= OnSettingsChanged;
        _requests.Writer.TryComplete();
        if (_lifetime is not null)
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }

        if (_worker is not null)
        {
            try
            {
                await _worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _lifetime?.Dispose();
    }
}
