using CodexUsageMonitor.Core.Usage;

namespace CodexUsageMonitor.Application.Monitoring;

public interface IUsageHistoryWriter
{
    Task RecordAsync(UsageSnapshot snapshot, CancellationToken cancellationToken);
}

public sealed class UsageHistoryWriteException : Exception
{
    public UsageHistoryWriteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
