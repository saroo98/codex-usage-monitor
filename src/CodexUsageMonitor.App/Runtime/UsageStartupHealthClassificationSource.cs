using CodexUsageMonitor.Application.Monitoring;
using CodexUsageMonitor.Application.Runtime;
using CodexUsageMonitor.Core.Monitoring;

namespace CodexUsageMonitor.App.Runtime;

public sealed class UsageStartupHealthClassificationSource : IStartupHealthClassificationSource, IDisposable
{
    private readonly UsageApplicationState _usage;
    private int _disposed;

    public UsageStartupHealthClassificationSource(UsageApplicationState usage)
    {
        _usage = usage ?? throw new ArgumentNullException(nameof(usage));
        _usage.MonitorStateChanged += OnMonitorStateChanged;
        _usage.ActiveProfileChanged += OnActiveProfileChanged;
    }

    public event EventHandler? ClassificationChanged;

    public bool IsClassified =>
        Volatile.Read(ref _disposed) == 0 &&
        _usage.ActiveMonitorState.Connection is not MonitorConnectionState.Starting;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _usage.MonitorStateChanged -= OnMonitorStateChanged;
        _usage.ActiveProfileChanged -= OnActiveProfileChanged;
    }

    private void OnMonitorStateChanged(object? sender, (Guid ProfileId, MonitorState State) value) =>
        ClassificationChanged?.Invoke(this, EventArgs.Empty);

    private void OnActiveProfileChanged(object? sender, Guid? profileId) =>
        ClassificationChanged?.Invoke(this, EventArgs.Empty);
}
