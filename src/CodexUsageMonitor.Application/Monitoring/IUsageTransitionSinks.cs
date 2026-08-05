using CodexUsageMonitor.Core.Notifications;
using CodexUsageMonitor.Core.Usage;

namespace CodexUsageMonitor.Application.Monitoring;

public interface IUsageNotificationSink
{
    Task<bool> DeliverAsync(
        UsageTransition transition,
        QuietHoursSchedule quietHours,
        bool playSound,
        CancellationToken cancellationToken);
}

public interface IUsageEmailSink
{
    bool IsConfigured { get; }

    Task<bool> QueueAsync(
        UsageTransition transition,
        UsageSnapshot snapshot,
        CancellationToken cancellationToken);
}
