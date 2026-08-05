using System.Diagnostics;
using CodexUsageMonitor.Application.Runtime;

namespace CodexUsageMonitor.App.Runtime;

public sealed class SystemApplicationProcessIdentity : IApplicationProcessIdentity
{
    public SystemApplicationProcessIdentity()
    {
        using var process = Process.GetCurrentProcess();
        ProcessId = process.Id;
        StartedAtUtc = process.StartTime.ToUniversalTime();
    }

    public int ProcessId { get; }

    public DateTimeOffset StartedAtUtc { get; }
}
