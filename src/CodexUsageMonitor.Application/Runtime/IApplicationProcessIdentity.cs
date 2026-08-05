namespace CodexUsageMonitor.Application.Runtime;

public interface IApplicationProcessIdentity
{
    int ProcessId { get; }

    DateTimeOffset StartedAtUtc { get; }
}
