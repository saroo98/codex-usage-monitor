using System.Windows;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.App.Runtime;

public sealed class ApplicationLifetimeController : IDisposable
{
    private static readonly TimeSpan ExitPreparationTimeout = TimeSpan.FromSeconds(20);
    private readonly System.Windows.Application _application;
    private readonly ILogger<ApplicationLifetimeController> _logger;
    private readonly CancellationTokenSource _applicationLifetime = new();
    private Func<CancellationToken, Task>? _exitPreparation;
    private int _exitRequested;
    private bool _disposed;

    public ApplicationLifetimeController(
        System.Windows.Application application,
        ILogger<ApplicationLifetimeController> logger)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public event EventHandler? ExitRequested;


    public event EventHandler<Guid>? UpdateRolledBack;

    public CancellationToken ApplicationToken => _applicationLifetime.Token;

    public bool IsExitRequested => Volatile.Read(ref _exitRequested) != 0;

    public void RegisterExitPreparation(Func<CancellationToken, Task> preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        if (Interlocked.CompareExchange(ref _exitPreparation, preparation, null) is not null)
        {
            throw new InvalidOperationException("Application exit preparation is already registered.");
        }
    }

    public void RequestExit()
    {
        if (Interlocked.Exchange(ref _exitRequested, 1) != 0)
        {
            return;
        }

        _logger.LogInformation("Application exit was requested.");
        _ = CompleteExitAsync();
    }

    public void NotifyUpdateRolledBack(Guid transactionId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(transactionId, Guid.Empty);

        UpdateRolledBack?.Invoke(this, transactionId);
    }

    public async Task CancelAsync()
    {
        if (!_applicationLifetime.IsCancellationRequested)
        {
            await _applicationLifetime.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task CompleteExitAsync()
    {
        var preparation = Volatile.Read(ref _exitPreparation);
        if (preparation is not null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_applicationLifetime.Token);
            timeout.CancelAfter(ExitPreparationTimeout);
            try
            {
                await preparation(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                _logger.LogWarning("Application exit preparation timed out or was cancelled; shutdown will continue.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _logger.LogWarning(exception, "Application exit preparation failed safely; shutdown will continue.");
            }
        }

        ExitRequested?.Invoke(this, EventArgs.Empty);
        await CancelAsync().ConfigureAwait(false);
        if (_application.Dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (_application.Dispatcher.CheckAccess())
        {
            _application.Shutdown();
        }
        else
        {
            await _application.Dispatcher.InvokeAsync(() => _application.Shutdown()).Task.ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _applicationLifetime.Dispose();
    }
}
