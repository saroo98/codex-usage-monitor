using System.Collections.Concurrent;
using CodexUsageMonitor.Application.Monitoring;
using CodexUsageMonitor.Core.Profiles;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.Codex.Monitoring;

public sealed class MultiProfileMonitorCoordinator : IProfileMonitorLifecycle, IAsyncDisposable
{
    private sealed record RunningProfile(
        ProfileDefinition Profile,
        CancellationTokenSource Lifetime,
        ProfileMonitorSupervisor Supervisor,
        Task Task);

    private readonly Func<ProfileMonitorSupervisor> _supervisorFactory;
    private readonly ILogger<MultiProfileMonitorCoordinator> _logger;
    private readonly ConcurrentDictionary<Guid, RunningProfile> _running = new();
    private readonly object _retiredGate = new();
    private readonly List<Task> _retired = [];
    private int _disposed;

    public MultiProfileMonitorCoordinator(
        Func<ProfileMonitorSupervisor> supervisorFactory,
        ILogger<MultiProfileMonitorCoordinator> logger)
    {
        _supervisorFactory = supervisorFactory ?? throw new ArgumentNullException(nameof(supervisorFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IReadOnlyCollection<Guid> RunningProfileIds => _running.Keys.ToArray();

    public void Reconcile(IEnumerable<ProfileDefinition> profiles, CancellationToken applicationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(profiles);
        var desired = profiles
            .Where(static profile => profile.Enabled && profile.MonitorInBackground)
            .GroupBy(static profile => profile.Id)
            .Select(static group => group.First())
            .ToDictionary(static profile => profile.Id);

        foreach (var item in _running.ToArray())
        {
            if (!desired.TryGetValue(item.Key, out var requested) || item.Value.Profile != requested)
            {
                Stop(item.Key, item.Value);
            }
        }

        foreach (var profile in desired.Values)
        {
            _running.GetOrAdd(profile.Id, _ => Start(profile, applicationToken));
        }

        ObserveRetiredTasks();
    }

    public bool RequestRefresh(Guid profileId, bool manual = true) =>
        _running.TryGetValue(profileId, out var running) && running.Supervisor.RequestRefresh(manual);

    public async Task<Contracts.ResetCreditConsumeResult?> ConsumeResetCreditAsync(
        Guid profileId,
        string? creditId,
        Guid idempotencyKey,
        string expectedAccountStorageKey,
        CancellationToken cancellationToken)
    {
        return _running.TryGetValue(profileId, out var running)
            ? await running.Supervisor.ConsumeResetCreditAsync(
                creditId,
                idempotencyKey,
                expectedAccountStorageKey,
                cancellationToken).ConfigureAwait(false)
            : null;
    }

    public int RequestRefreshAll(bool manual = true)
    {
        var accepted = 0;
        foreach (var running in _running.Values)
        {
            if (running.Supervisor.RequestRefresh(manual))
            {
                accepted++;
            }
        }

        return accepted;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var running = _running.ToArray();
        foreach (var item in running)
        {
            Stop(item.Key, item.Value);
        }

        Task[] tasks;
        lock (_retiredGate)
        {
            tasks = _retired.ToArray();
        }

        if (tasks.Length == 0)
        {
            return;
        }

        await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
        ObserveRetiredTasks();
    }

    private RunningProfile Start(ProfileDefinition profile, CancellationToken applicationToken)
    {
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(applicationToken);
        var supervisor = _supervisorFactory();
        var task = RunGuardedAsync(profile, supervisor, lifetime.Token);
        _logger.LogInformation("Started Codex monitor for profile {ProfileId}.", profile.Id);
        return new RunningProfile(profile, lifetime, supervisor, task);
    }

    private async Task RunGuardedAsync(
        ProfileDefinition profile,
        ProfileMonitorSupervisor supervisor,
        CancellationToken cancellationToken)
    {
        try
        {
            await supervisor.RunAsync(profile, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Codex monitor for profile {ProfileId} stopped unexpectedly.", profile.Id);
        }
    }

    private void Stop(Guid profileId, RunningProfile running)
    {
        if (!((ICollection<KeyValuePair<Guid, RunningProfile>>)_running).Remove(
                new KeyValuePair<Guid, RunningProfile>(profileId, running)))
        {
            return;
        }

        running.Lifetime.Cancel();
        lock (_retiredGate)
        {
            _retired.Add(running.Task.ContinueWith(
                static (_, state) => ((CancellationTokenSource)state!).Dispose(),
                running.Lifetime,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default));
        }

        _logger.LogInformation("Stopping Codex monitor for profile {ProfileId}.", profileId);
    }

    private void ObserveRetiredTasks()
    {
        lock (_retiredGate)
        {
            _retired.RemoveAll(static task =>
            {
                if (!task.IsCompleted)
                {
                    return false;
                }

                _ = task.Exception;
                return true;
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Timed out while stopping Codex profile monitors.");
        }
    }
}
