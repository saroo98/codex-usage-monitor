using System.Net.Http;
using CodexUsageMonitor.Codex.Transport;
using CodexUsageMonitor.Notifications.Native;
using CodexUsageMonitor.Persistence.Paths;
using CodexUsageMonitor.Persistence.Settings;
using CodexUsageMonitor.Windows.Runtime;
using CodexUsageMonitor.Windows.Startup;

namespace CodexUsageMonitor.App.Runtime;

public sealed record AppHostOptions
{
    public AppDataPaths? Paths { get; init; }

    public ISettingsStore? SettingsStore { get; init; }

    public INativeNotificationService? NativeNotificationService { get; init; }

    public IStartupRegistration? StartupRegistration { get; init; }

    public Func<IProcessContainment>? ProcessContainmentFactory { get; init; }

    public HttpMessageHandler? HttpMessageHandler { get; init; }

    public SingleInstanceCoordinator? SingleInstanceCoordinator { get; init; }

    public bool TestMode { get; init; }

    public bool AllowUnsignedDevelopmentUpdates { get; init; }
}
