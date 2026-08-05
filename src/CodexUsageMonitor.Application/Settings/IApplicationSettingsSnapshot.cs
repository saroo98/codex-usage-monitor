using CodexUsageMonitor.Core.Settings;

namespace CodexUsageMonitor.Application.Settings;

public interface IApplicationSettingsSnapshot
{
    AppSettings Current { get; }

    event EventHandler<AppSettings>? Changed;
}
