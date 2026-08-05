using CodexUsageMonitor.Application.Updates;
using CodexUsageMonitor.Core.Abstractions;
using CodexUsageMonitor.Persistence.Paths;
using CodexUsageMonitor.Updater;
using CodexUsageMonitor.Updater.Install;
using CodexUsageMonitor.Updater.Network;
using CodexUsageMonitor.Updater.Model;
using CodexUsageMonitor.Updater.Staging;
using CodexUsageMonitor.Windows.Startup;

namespace CodexUsageMonitor.App.Services;

public sealed class UpdatePlatformAdapter : IUpdatePlatformPort
{
    private readonly CodexUsageMonitor.Updater.UpdateCheckService _checks;
    private readonly UpdateAssetDownloader _downloader;
    private readonly PortableUpdateStager _stager;
    private readonly PortableUpdateLauncher _launcher;
    private readonly AppDataPaths _paths;
    private readonly IClock _clock;
    private readonly IApplicationPackageContext _packageContext;
    private readonly IApplicationProcessIdentity _processIdentity;
    private readonly SemanticVersion _currentVersion;
    private UpdateCheckResult? _candidate;
    private StagedUpdate? _staged;

    public UpdatePlatformAdapter(
        CodexUsageMonitor.Updater.UpdateCheckService checks,
        UpdateAssetDownloader downloader,
        PortableUpdateStager stager,
        PortableUpdateLauncher launcher,
        AppDataPaths paths,
        IClock clock,
        IApplicationPackageContext packageContext,
        IApplicationProcessIdentity processIdentity,
        SemanticVersion currentVersion)
    {
        _checks = checks;
        _downloader = downloader;
        _stager = stager;
        _launcher = launcher;
        _paths = paths;
        _clock = clock;
        _packageContext = packageContext;
        _processIdentity = processIdentity;
        _currentVersion = currentVersion;
    }

    public bool IsManagedExternally => _packageContext.IsPackaged;

    public bool HasVerifiedCandidate => _candidate?.Asset is not null;

    public bool HasPreparedUpdate => _staged is not null;

    public async Task<UpdateCheckOutcome> CheckAsync(
        Uri manifestUri,
        string channel,
        string? entityTag,
        CancellationToken cancellationToken)
    {
        var result = await _checks.CheckAsync(manifestUri, channel, entityTag, cancellationToken).ConfigureAwait(false);
        if (result.Status is not UpdateCheckStatus.NotModified)
        {
            _candidate = result.Status is UpdateCheckStatus.Available && result.Asset is not null ? result : null;
            _staged = null;
        }

        return new UpdateCheckOutcome(
            result.Status switch
            {
                UpdateCheckStatus.NotModified => UpdateCheckOutcomeStatus.NotModified,
                UpdateCheckStatus.Current => UpdateCheckOutcomeStatus.Current,
                UpdateCheckStatus.Available => UpdateCheckOutcomeStatus.Available,
                UpdateCheckStatus.UnsupportedOperatingSystem => UpdateCheckOutcomeStatus.UnsupportedOperatingSystem,
                UpdateCheckStatus.UnsupportedArchitecture => UpdateCheckOutcomeStatus.UnsupportedArchitecture,
                _ => throw new InvalidOperationException("Unknown update status."),
            },
            result.Manifest?.Version,
            result.Manifest?.ReleaseNotesUrl,
            result.EntityTag,
            result.Asset is not null);
    }

    public async Task PrepareAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var manifest = _candidate?.Manifest ?? throw new InvalidOperationException("A verified update manifest is unavailable.");
        var asset = _candidate.Asset ?? throw new InvalidOperationException("A verified update asset is unavailable.");
        var downloadDirectory = Path.Combine(_paths.UpdatesDirectory, "downloads", manifest.Version);
        var downloaded = await _downloader.DownloadAsync(asset, downloadDirectory, progress, cancellationToken).ConfigureAwait(false);
        _staged = await _stager.StageAsync(
            manifest,
            asset,
            downloaded.FilePath,
            AppContext.BaseDirectory,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task LaunchPreparedAsync(CancellationToken cancellationToken)
    {
        var staged = _staged ?? throw new InvalidOperationException("A staged update is unavailable.");
        var currentExecutable = Path.Combine(AppContext.BaseDirectory, UpdatePathLayout.ApplicationExecutableName);
        var currentApplicationSha256 = await UpdateFileIntegrity.ComputeSha256Async(currentExecutable, cancellationToken).ConfigureAwait(false);
        var request = UpdateInstallRequest.Create(
            staged.Version,
            _currentVersion.ToString(),
            _processIdentity.ProcessId,
            _processIdentity.StartedAtUtc,
            AppContext.BaseDirectory,
            staged.StagingDirectory,
            _paths.IsPortable,
            currentApplicationSha256,
            staged.ApplicationSha256,
            staged.UpdaterSha256,
            staged.PackageFileManifestSha256,
            staged.TrustMode,
            staged.PublisherThumbprints,
            _clock.UtcNow);
        await _launcher.LaunchAsync(request, staged.UpdaterExecutable, cancellationToken).ConfigureAwait(false);
    }
}
