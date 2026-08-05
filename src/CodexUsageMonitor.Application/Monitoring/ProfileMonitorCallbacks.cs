using CodexUsageMonitor.Core.Monitoring;
using CodexUsageMonitor.Core.Profiles;
using CodexUsageMonitor.Core.Usage;

namespace CodexUsageMonitor.Application.Monitoring;

public interface IProfileMonitorCallbacks
{
    ValueTask SnapshotReceivedAsync(ProfileDefinition profile, UsageSnapshot snapshot, CancellationToken cancellationToken);

    ValueTask StateChangedAsync(ProfileDefinition profile, MonitorState state, CancellationToken cancellationToken);
}
