namespace CodexUsageMonitor.Codex.Transport;

public interface IJsonLineTransport : IAsyncDisposable
{
    bool IsConnected { get; }

    IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken);

    ValueTask WriteLineAsync(string line, CancellationToken cancellationToken);
}
