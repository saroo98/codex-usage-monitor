namespace CodexUsageMonitor.Application.Diagnostics;

public interface IApplicationFailureSink
{
    void Report(string safeCode, Exception exception, Guid? profileId = null);
}
