namespace CodexUsageMonitor.Windows.Startup;

public enum StartupRegistrationState
{
    Disabled,
    Enabled,
    EnabledByPolicy,
    DisabledByPolicy,
    Unavailable,
}

public sealed record StartupRegistrationResult(StartupRegistrationState State, string? SafeReasonCode = null)
{
    public bool IsEnabled => State is StartupRegistrationState.Enabled or StartupRegistrationState.EnabledByPolicy;
    public bool CanChange => State is StartupRegistrationState.Enabled or StartupRegistrationState.Disabled;
}

public interface IStartupRegistration
{
    Task<StartupRegistrationResult> GetStateAsync(CancellationToken cancellationToken);

    Task<StartupRegistrationResult> SetEnabledAsync(bool enabled, CancellationToken cancellationToken);
}
