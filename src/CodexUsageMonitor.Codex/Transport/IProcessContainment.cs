using System.Diagnostics;

namespace CodexUsageMonitor.Codex.Transport;

public interface IProcessContainment : IDisposable
{
    void Attach(Process process);
}

public sealed class NullProcessContainment : IProcessContainment
{
    public static NullProcessContainment Instance { get; } = new();

    private NullProcessContainment()
    {
    }

    public void Attach(Process process)
    {
    }

    public void Dispose()
    {
    }
}
