using CodexUsageMonitor.Updater.Install;
using CodexUsageMonitor.Updater.Manifest;

namespace CodexUsageMonitor.Updater.Staging;

public sealed record StagedUpdate(
    string Version,
    string StagingDirectory,
    string ApplicationExecutable,
    string UpdaterExecutable,
    string ApplicationSha256,
    string UpdaterSha256,
    string PackageFileManifestSha256,
    UpdateArtifactTrustMode TrustMode,
    IReadOnlyList<string> PublisherThumbprints,
    DateTimeOffset StagedAtUtc);

public sealed class PortableUpdateStager
{
    private readonly SafeZipExtractor _extractor;
    private readonly UpdateArtifactTrustPolicy _trustPolicy;

    public PortableUpdateStager(
        SafeZipExtractor extractor,
        UpdateArtifactTrustPolicy trustPolicy)
    {
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _trustPolicy = trustPolicy ?? throw new ArgumentNullException(nameof(trustPolicy));
    }

    public async Task<StagedUpdate> StageAsync(
        UpdateManifestDocument manifest,
        UpdateAsset asset,
        string downloadedArchivePath,
        string installationDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(asset);
        var install = UpdatePathLayout.NormalizeInstallationDirectory(installationDirectory);
        var staging = UpdatePathLayout.GetStagingDirectory(install, manifest.Version);
        UpdatePathSecurity.EnsureDescendant(
            staging,
            UpdatePathLayout.GetStagingRoot(install),
            "The update staging directory escaped its root.");
        if (UpdatePathSecurity.PathEntryExists(staging))
        {
            UpdatePathSecurity.DeleteDirectoryTree(
                staging,
                "The existing update staging directory is unsafe to replace.");
        }

        try
        {
            await _extractor.ExtractAsync(downloadedArchivePath, staging, cancellationToken).ConfigureAwait(false);
            UpdatePathSecurity.EnsureDirectoryTreeWithoutReparsePoints(
                staging,
                "The extracted update staging directory is unsafe.");
            PortableUpdateData.ValidateReservedPayloadPaths(staging);
            var appExecutable = Path.Combine(staging, UpdatePathLayout.ApplicationExecutableName);
            var updaterExecutable = Path.Combine(staging, UpdatePathLayout.UpdaterHostExecutableName);
            UpdatePathSecurity.EnsureRegularFile(
                appExecutable,
                "Update package does not contain a valid application executable.");
            UpdatePathSecurity.EnsureRegularFile(
                updaterExecutable,
                "Update package does not contain a valid updater host executable.");

            var packageManifest = await UpdatePackageFileManifest.ReadAndVerifyAsync(
                staging,
                manifest.Version,
                cancellationToken).ConfigureAwait(false);
            var publisherThumbprints = UpdatePublisherPins.Normalize(
                asset.PublisherThumbprints,
                allowEmpty: true);
            var trustMode = await _trustPolicy.VerifyStagedExecutablesAsync(
                appExecutable,
                updaterExecutable,
                publisherThumbprints,
                packageManifest,
                cancellationToken).ConfigureAwait(false);
            if (trustMode is UpdateArtifactTrustMode.PublisherSignature)
            {
                UpdatePublisherPins.ValidateCanonical(publisherThumbprints);
            }

            var applicationEntry = packageManifest.GetRequiredEntry(UpdatePathLayout.ApplicationExecutableName);
            var updaterEntry = packageManifest.GetRequiredEntry(UpdatePathLayout.UpdaterHostExecutableName);
            return new StagedUpdate(
                manifest.Version,
                staging,
                appExecutable,
                updaterExecutable,
                applicationEntry.Sha256,
                updaterEntry.Sha256,
                packageManifest.ManifestSha256,
                trustMode,
                publisherThumbprints,
                DateTimeOffset.UtcNow);
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (UpdatePathSecurity.PathEntryExists(path))
            {
                UpdatePathSecurity.DeleteDirectoryTree(
                    path,
                    "The update staging directory is unsafe to delete.");
            }
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            InvalidDataException)
        {
        }
    }
}
