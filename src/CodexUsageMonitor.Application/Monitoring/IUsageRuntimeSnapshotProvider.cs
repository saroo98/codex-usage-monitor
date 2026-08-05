using CodexUsageMonitor.Core.Monitoring;
using CodexUsageMonitor.Core.Usage;

namespace CodexUsageMonitor.Application.Monitoring;

public interface IUsageRuntimeSnapshotProvider
{
    UsageSnapshot? ActiveSnapshot { get; }

    MonitorState ActiveMonitorState { get; }
}
