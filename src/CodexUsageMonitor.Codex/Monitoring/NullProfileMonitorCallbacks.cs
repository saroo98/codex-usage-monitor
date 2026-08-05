using CodexUsageMonitor.Application.Monitoring;
using CodexUsageMonitor.Core.Monitoring;
using CodexUsageMonitor.Core.Profiles;
using CodexUsageMonitor.Core.Usage;

namespace CodexUsageMonitor.Codex.Monitoring;

public sealed class NullProfileMonitorCallbacks : IProfileMonitorCallbacks
{
    public static NullProfileMonitorCallbacks Instance { get; } = new();

    private NullProfileMonitorCallbacks()
    {
    }

    public ValueTask SnapshotReceivedAsync(ProfileDefinition profile, UsageSnapshot snapshot, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask StateChangedAsync(ProfileDefinition profile, MonitorState state, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
