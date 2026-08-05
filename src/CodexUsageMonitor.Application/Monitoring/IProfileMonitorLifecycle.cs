using CodexUsageMonitor.Core.Profiles;

namespace CodexUsageMonitor.Application.Monitoring;

public interface IProfileMonitorLifecycle
{
    IReadOnlyCollection<Guid> RunningProfileIds { get; }

    void Reconcile(IEnumerable<ProfileDefinition> profiles, CancellationToken applicationToken);

    int RequestRefreshAll(bool manual = true);

    Task StopAsync(CancellationToken cancellationToken);
}
