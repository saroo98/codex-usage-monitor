using Windows.ApplicationModel;

namespace CodexUsageMonitor.Windows.Startup;

public sealed class PackagedStartupRegistration : IStartupRegistration
{
    private readonly string _taskId;

    public PackagedStartupRegistration(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        _taskId = taskId;
    }

    public async Task<StartupRegistrationResult> GetStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var task = await StartupTask.GetAsync(_taskId).AsTask(cancellationToken).ConfigureAwait(false);
            return Map(task.State);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new StartupRegistrationResult(StartupRegistrationState.Unavailable, "startup.package_task_unavailable");
        }
    }

    public async Task<StartupRegistrationResult> SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var task = await StartupTask.GetAsync(_taskId).AsTask(cancellationToken).ConfigureAwait(false);
            if (!enabled)
            {
                task.Disable();
                return Map(task.State);
            }

            if (task.State is StartupTaskState.Disabled)
            {
                return Map(await task.RequestEnableAsync().AsTask(cancellationToken).ConfigureAwait(false));
            }

            return Map(task.State);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new StartupRegistrationResult(StartupRegistrationState.Unavailable, "startup.package_task_unavailable");
        }
    }

    private static StartupRegistrationResult Map(StartupTaskState state) => state switch
    {
        StartupTaskState.Enabled => new(StartupRegistrationState.Enabled),
        StartupTaskState.EnabledByPolicy => new(StartupRegistrationState.EnabledByPolicy, "startup.enabled_by_policy"),
        StartupTaskState.DisabledByPolicy => new(StartupRegistrationState.DisabledByPolicy, "startup.disabled_by_policy"),
        StartupTaskState.DisabledByUser => new(StartupRegistrationState.DisabledByPolicy, "startup.disabled_by_user"),
        _ => new(StartupRegistrationState.Disabled),
    };
}
