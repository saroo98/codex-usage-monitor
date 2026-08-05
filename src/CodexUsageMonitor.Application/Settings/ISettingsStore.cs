using CodexUsageMonitor.Core.Settings;

namespace CodexUsageMonitor.Application.Settings;

public interface ISettingsStore
{
    Task<SettingsValidationResult> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);
}
