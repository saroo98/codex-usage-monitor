using CodexUsageMonitor.Codex.Protocol;
using CodexUsageMonitor.Codex.Transport;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.Codex;

public sealed class AppServerClientFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly Func<IProcessContainment> _containmentFactory;

    public AppServerClientFactory(
        ILoggerFactory loggerFactory,
        Func<IProcessContainment> containmentFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _containmentFactory = containmentFactory ?? throw new ArgumentNullException(nameof(containmentFactory));
    }

    public IAppServerClient Create(ResolvedCodexCommand command, string? codexHome)
    {
        var transport = ProcessJsonLineTransport.Start(
            command,
            codexHome,
            _containmentFactory(),
            _loggerFactory.CreateLogger<ProcessJsonLineTransport>());
        var connection = new JsonRpcConnection(transport, _loggerFactory.CreateLogger<JsonRpcConnection>());
        return new AppServerClient(connection, _loggerFactory.CreateLogger<AppServerClient>());
    }
}
