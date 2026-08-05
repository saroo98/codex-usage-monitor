using CodexUsageMonitor.Application.Diagnostics;
using CodexUsageMonitor.Application.Runtime;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.App.Services;

public sealed class SynchronizationContextEventDispatcher(SynchronizationContext context) : IApplicationEventDispatcher
{
    private readonly SynchronizationContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _context.Post(static value => ((Action)value!).Invoke(), action);
    }
}

public sealed class LoggingApplicationFailureSink(ILogger<LoggingApplicationFailureSink> logger) : IApplicationFailureSink
{
    private readonly ILogger<LoggingApplicationFailureSink> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public void Report(string safeCode, Exception exception, Guid? profileId = null) =>
        _logger.LogWarning(exception, "Application operation {SafeCode} failed for profile {ProfileId}.", safeCode, profileId);
}
