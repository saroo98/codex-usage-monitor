using CodexUsageMonitor.Application.Diagnostics;
using CodexUsageMonitor.Application.Settings;
using CodexUsageMonitor.Core.Settings;

namespace CodexUsageMonitor.Application.Monitoring;

public sealed class ProfileMonitoringCoordinatorService : IAsyncDisposable
{
    private readonly IApplicationSettingsSnapshot _settings;
    private readonly IProfileMonitorLifecycle _monitors;
    private readonly IApplicationFailureSink _failures;
    private readonly SemaphoreSlim _restartGate = new(1, 1);
    private CancellationToken _applicationToken;
    private int _started;
    private int _restartInProgress;
    private int _reconcilePending;

    public ProfileMonitoringCoordinatorService(
        IApplicationSettingsSnapshot settings,
        IProfileMonitorLifecycle monitors,
        IApplicationFailureSink failures)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _monitors = monitors ?? throw new ArgumentNullException(nameof(monitors));
        _failures = failures ?? throw new ArgumentNullException(nameof(failures));
    }

    public void Start(CancellationToken applicationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _applicationToken = applicationToken;
        _settings.Changed += OnSettingsChanged;
        Reconcile(_settings.Current);
    }

    public void RefreshAll() => _monitors.RequestRefreshAll(manual: false);

    public async Task<int> RestartAllAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _started) == 0)
        {
            return 0;
        }

        await _restartGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _restartInProgress, 1);
        try
        {
            await _monitors.StopAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (_applicationToken.IsCancellationRequested)
            {
                return 0;
            }

            Reconcile(_settings.Current);
            return _monitors.RunningProfileIds.Count;
        }
        finally
        {
            Interlocked.Exchange(ref _restartInProgress, 0);
            if (Interlocked.Exchange(ref _reconcilePending, 0) != 0 && !_applicationToken.IsCancellationRequested)
            {
                Reconcile(_settings.Current);
            }

            _restartGate.Release();
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        if (Volatile.Read(ref _restartInProgress) != 0)
        {
            Interlocked.Exchange(ref _reconcilePending, 1);
            return;
        }

        Reconcile(settings);
    }

    private void Reconcile(AppSettings settings)
    {
        try
        {
            _monitors.Reconcile(settings.Profiles.Select(static profile => profile.ToDefinition()), _applicationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            _failures.Report("monitoring.reconcile_failed", exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
        {
            return;
        }

        _settings.Changed -= OnSettingsChanged;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await _monitors.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _failures.Report("monitoring.stop_timeout", new TimeoutException("Timed out while stopping profile monitoring."));
        }
    }
}
