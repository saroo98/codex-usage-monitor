namespace CodexUsageMonitor.Core.Diagnostics;

public interface IProtocolAnomalySink
{
    void Report(string code, IReadOnlyDictionary<string, string>? safeContext = null);
}

public sealed class NullProtocolAnomalySink : IProtocolAnomalySink
{
    public static NullProtocolAnomalySink Instance { get; } = new();

    private NullProtocolAnomalySink()
    {
    }

    public void Report(string code, IReadOnlyDictionary<string, string>? safeContext = null)
    {
    }
}
