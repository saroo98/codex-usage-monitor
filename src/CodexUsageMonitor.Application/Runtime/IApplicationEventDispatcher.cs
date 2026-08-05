namespace CodexUsageMonitor.Application.Runtime;

public interface IApplicationEventDispatcher
{
    void Post(Action action);
}
