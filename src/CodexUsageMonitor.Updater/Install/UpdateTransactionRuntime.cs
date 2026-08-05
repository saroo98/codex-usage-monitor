using System.ComponentModel;
using System.Diagnostics;

namespace CodexUsageMonitor.Updater.Install;

public enum UpdateParentExitResult
{
    Exited,
    TimedOut,
    IdentityMismatch,
}

public enum UpdateApplicationLaunchMode
{
    Normal,
    AfterUpdate,
    RolledBack,
}

public interface IUpdateApplicationProcess : IDisposable
{
    int ProcessId { get; }

    DateTimeOffset StartedAtUtc { get; }

    bool HasExited { get; }

    void Terminate();
}

public interface IUpdateTransactionRuntime
{
    DateTimeOffset UtcNow { get; }

    Task<UpdateParentExitResult> WaitForParentExitAsync(
        int processId,
        DateTimeOffset expectedStartedAtUtc,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    IUpdateApplicationProcess StartApplication(
        UpdateTransactionJournal journal,
        UpdateApplicationLaunchMode launchMode);

    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemUpdateTransactionRuntime : IUpdateTransactionRuntime
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public async Task<UpdateParentExitResult> WaitForParentExitAsync(
        int processId,
        DateTimeOffset expectedStartedAtUtc,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (processId <= 0)
        {
            return UpdateParentExitResult.IdentityMismatch;
        }

        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return UpdateParentExitResult.Exited;
        }

        using (process)
        {
            DateTimeOffset actualStartedAtUtc;
            try
            {
                actualStartedAtUtc = process.StartTime.ToUniversalTime();
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                return TryHasExited(process)
                    ? UpdateParentExitResult.Exited
                    : UpdateParentExitResult.IdentityMismatch;
            }

            if (actualStartedAtUtc != expectedStartedAtUtc.ToUniversalTime())
            {
                return UpdateParentExitResult.IdentityMismatch;
            }

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
                return UpdateParentExitResult.Exited;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return UpdateParentExitResult.TimedOut;
            }
        }
    }

    public IUpdateApplicationProcess StartApplication(
        UpdateTransactionJournal journal,
        UpdateApplicationLaunchMode launchMode)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var executable = Path.Combine(journal.InstallationDirectory, journal.ApplicationExecutableName);
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = journal.InstallationDirectory,
            UseShellExecute = false,
        };
        switch (launchMode)
        {
            case UpdateApplicationLaunchMode.AfterUpdate:
                start.ArgumentList.Add("--after-update");
                start.ArgumentList.Add(journal.TransactionId.ToString("D"));
                start.ArgumentList.Add("--health-marker");
                start.ArgumentList.Add(journal.HealthMarkerPath);
                break;
            case UpdateApplicationLaunchMode.RolledBack:
                start.ArgumentList.Add("--update-rolled-back");
                start.ArgumentList.Add(journal.TransactionId.ToString("D"));
                break;
            case UpdateApplicationLaunchMode.Normal:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(launchMode));
        }

        var process = Process.Start(start)
            ?? throw new InvalidOperationException("Application could not be started after the update transaction.");
        try
        {
            return new SystemUpdateApplicationProcess(
                process,
                process.StartTime.ToUniversalTime());
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
            }

            process.Dispose();
            throw;
        }
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);

    private static bool TryHasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            return true;
        }
    }

    private sealed class SystemUpdateApplicationProcess : IUpdateApplicationProcess
    {
        private readonly Process _process;
        private bool _disposed;

        public SystemUpdateApplicationProcess(Process process, DateTimeOffset startedAtUtc)
        {
            _process = process ?? throw new ArgumentNullException(nameof(process));
            ProcessId = process.Id;
            StartedAtUtc = startedAtUtc.ToUniversalTime();
        }

        public int ProcessId { get; }

        public DateTimeOffset StartedAtUtc { get; }

        public bool HasExited
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                try
                {
                    return _process.HasExited;
                }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
                {
                    return true;
                }
            }
        }

        public void Terminate()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(5_000);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _process.Dispose();
        }
    }
}
