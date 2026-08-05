using System.IO;
using Microsoft.Win32;

namespace CodexUsageMonitor.Windows.Startup;

public sealed class PortableStartupRegistration : IStartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _valueName;
    private readonly string _executablePath;

    public PortableStartupRegistration(string valueName, string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _valueName = valueName;
        _executablePath = Path.GetFullPath(executablePath);
    }

    public Task<StartupRegistrationResult> GetStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var configured = key?.GetValue(_valueName) as string;
            var expected = BuildCommandLine();
            return Task.FromResult(new StartupRegistrationResult(
                string.Equals(configured, expected, StringComparison.OrdinalIgnoreCase)
                    ? StartupRegistrationState.Enabled
                    : StartupRegistrationState.Disabled));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(new StartupRegistrationResult(StartupRegistrationState.DisabledByPolicy, "startup.registry_denied"));
        }
        catch (System.Security.SecurityException)
        {
            return Task.FromResult(new StartupRegistrationResult(StartupRegistrationState.DisabledByPolicy, "startup.registry_policy"));
        }
    }

    public Task<StartupRegistrationResult> SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new InvalidOperationException("The current-user startup registry key is unavailable.");
            if (enabled)
            {
                key.SetValue(_valueName, BuildCommandLine(), RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(_valueName, throwOnMissingValue: false);
            }

            return Task.FromResult(new StartupRegistrationResult(
                enabled ? StartupRegistrationState.Enabled : StartupRegistrationState.Disabled));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(new StartupRegistrationResult(StartupRegistrationState.DisabledByPolicy, "startup.registry_denied"));
        }
        catch (System.Security.SecurityException)
        {
            return Task.FromResult(new StartupRegistrationResult(StartupRegistrationState.DisabledByPolicy, "startup.registry_policy"));
        }
    }

    private string BuildCommandLine() => $"\"{_executablePath}\" --background";
}
