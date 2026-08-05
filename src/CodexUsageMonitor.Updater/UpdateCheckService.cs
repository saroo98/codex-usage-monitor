using System.Runtime.InteropServices;
using CodexUsageMonitor.Updater.Manifest;
using CodexUsageMonitor.Updater.Model;
using CodexUsageMonitor.Updater.Network;

namespace CodexUsageMonitor.Updater;

public enum UpdateCheckStatus
{
    NotModified,
    Current,
    Available,
    UnsupportedOperatingSystem,
    UnsupportedArchitecture,
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    UpdateManifestDocument? Manifest,
    UpdateAsset? Asset,
    string? EntityTag);

public sealed class UpdateCheckService
{
    private readonly UpdateManifestClient _client;
    private readonly SemanticVersion _currentVersion;

    public UpdateCheckService(UpdateManifestClient client, SemanticVersion currentVersion)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _currentVersion = currentVersion;
    }

    public async Task<UpdateCheckResult> CheckAsync(
        Uri manifestUri,
        string channel,
        string? entityTag,
        CancellationToken cancellationToken)
    {
        var fetch = await _client.FetchAsync(manifestUri, entityTag, cancellationToken).ConfigureAwait(false);
        if (fetch.NotModified)
        {
            return new UpdateCheckResult(UpdateCheckStatus.NotModified, null, null, fetch.EntityTag);
        }

        var manifest = fetch.Manifest!;
        if (!string.Equals(manifest.Channel, channel, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Update manifest channel does not match the selected channel.");
        }

        if (Environment.OSVersion.Version.Build < manifest.MinimumOsBuild)
        {
            return new UpdateCheckResult(UpdateCheckStatus.UnsupportedOperatingSystem, manifest, null, fetch.EntityTag);
        }

        var architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => UpdateArchitecture.X64,
            Architecture.Arm64 => UpdateArchitecture.Arm64,
            _ => (UpdateArchitecture?)null,
        };
        if (architecture is null)
        {
            return new UpdateCheckResult(UpdateCheckStatus.UnsupportedArchitecture, manifest, null, fetch.EntityTag);
        }

        if (manifest.ParsedVersion <= _currentVersion)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Current, manifest, null, fetch.EntityTag);
        }

        return new UpdateCheckResult(
            UpdateCheckStatus.Available,
            manifest,
            manifest.SelectAsset(architecture.Value),
            fetch.EntityTag);
    }
}
