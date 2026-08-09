using System.ComponentModel;
using CodexUsageMonitor.Core.Security;
using CodexUsageMonitor.Core.Settings;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.App.Services;

public sealed class ObsoleteEmailCredentialCleanup
{
    private readonly ApplicationSettingsService _settings;
    private readonly ISecretStore _secrets;
    private readonly ILogger<ObsoleteEmailCredentialCleanup> _logger;

    public ObsoleteEmailCredentialCleanup(
        ApplicationSettingsService settings,
        ISecretStore secrets,
        ILogger<ObsoleteEmailCredentialCleanup> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var references = _settings.Current.Email.ObsoleteSecretReferences
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (references.Length == 0)
        {
            return;
        }

        var remaining = new List<string>();
        foreach (var reference in references)
        {
            try
            {
                await _secrets.DeleteAsync(reference, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
            {
                remaining.Add(reference);
                _logger.LogWarning(exception, "An obsolete email credential could not be removed from Windows-protected storage.");
            }
        }

        if (_settings.CanPersist)
        {
            await _settings.UpdateAsync(settings => settings with
            {
                Email = settings.Email with { ObsoleteSecretReferences = remaining },
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
