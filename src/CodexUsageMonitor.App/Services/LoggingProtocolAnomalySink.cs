using CodexUsageMonitor.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.App.Services;

public sealed class LoggingProtocolAnomalySink(ILogger<LoggingProtocolAnomalySink> logger) : IProtocolAnomalySink
{
    public void Report(string code, IReadOnlyDictionary<string, string>? safeContext = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var context = safeContext is null
            ? string.Empty
            : string.Join(';', safeContext.Take(8).Select(static pair =>
                $"{SafeDiagnosticRedactor.Redact(pair.Key)}={SafeDiagnosticRedactor.Redact(pair.Value)}"));
        logger.LogWarning("Protocol anomaly {Code}. {SafeContext}", SafeDiagnosticRedactor.Redact(code), context);
    }
}
