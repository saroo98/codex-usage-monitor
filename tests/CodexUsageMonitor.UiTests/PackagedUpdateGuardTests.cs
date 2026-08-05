using System.Net;
using System.Net.Http;
using CodexUsageMonitor.Application.Diagnostics;
using CodexUsageMonitor.App.Runtime;
using CodexUsageMonitor.App.Services;
using CodexUsageMonitor.Core.Abstractions;
using CodexUsageMonitor.Core.Settings;
using CodexUsageMonitor.Persistence.Paths;
using CodexUsageMonitor.Persistence.Settings;
using CodexUsageMonitor.Updater;
using CodexUsageMonitor.Updater.Install;
using CodexUsageMonitor.Updater.Manifest;
using CodexUsageMonitor.Updater.Model;
using CodexUsageMonitor.Updater.Network;
using CodexUsageMonitor.Updater.Security;
using CodexUsageMonitor.Updater.Staging;
using CodexUsageMonitor.Windows.Startup;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexUsageMonitor.UiTests;

[TestClass]
public sealed class PackagedUpdateGuardTests
{
    [TestMethod]
    public async Task PackagedApplicationNeverUsesPortableUpdatePipeline()
    {
        var handler = new RejectingHandler();
        using var http = new HttpClient(handler);
        var settings = new ApplicationSettingsService(new InMemorySettingsStore());
        var version = new SemanticVersion(1, 0, 0);
        var manifestVerifier = new ManifestSignatureVerifier(new byte[32]);
        var manifestClient = new UpdateManifestClient(
            http,
            new UpdateManifestValidator(),
            manifestVerifier);
        var checks = new UpdateCheckService(manifestClient, version);
        var root = Path.Combine(Path.GetTempPath(), "cum-packaged-update", Guid.NewGuid().ToString("N"));
        var paths = new AppDataPaths(
            root,
            Path.Combine(root, "settings.json"),
            Path.Combine(root, "data.db"),
            Path.Combine(root, "logs"),
            Path.Combine(root, "cache"),
            Path.Combine(root, "updates"),
            Path.Combine(root, "support"),
            IsPortable: false);
        var state = new UpdateRuntimeState(version.ToString());
        var trustPolicy = new UpdateArtifactTrustPolicy(
            new NeverTrustedSignatureVerifier(),
            UpdateTrustPolicyOptions.Production);
        var platform = new UpdatePlatformAdapter(
            checks,
            new UpdateAssetDownloader(http),
            new PortableUpdateStager(new SafeZipExtractor(), trustPolicy),
            new PortableUpdateLauncher(trustPolicy, new NeverStartedUpdaterHost()),
            paths,
            new FixedClock(),
            new PackagedContext(),
            new FixedProcessIdentity(),
            version);
        var coordinator = new UpdateCoordinatorService(
            platform,
            settings,
            state,
            new FixedClock(),
            new RejectingFailureSink());

        var checkedState = await coordinator.CheckAsync(manual: true, CancellationToken.None);
        var preparedState = await coordinator.PrepareAsync(CancellationToken.None);
        var installedState = await coordinator.InstallPreparedAsync(CancellationToken.None);

        Assert.AreEqual(UpdateRuntimeStatus.ManagedExternally, checkedState.Status);
        Assert.AreEqual(UpdateRuntimeStatus.ManagedExternally, preparedState.Status);
        Assert.AreEqual(UpdateRuntimeStatus.ManagedExternally, installedState.Status);
        Assert.AreEqual("update.msix_managed_externally", installedState.SafeErrorCode);
        Assert.AreEqual(0, handler.CallCount);
        Assert.IsFalse(Directory.Exists(paths.UpdatesDirectory));
    }

    private sealed class PackagedContext : IApplicationPackageContext
    {
        public bool IsPackaged => true;
    }


    private sealed class FixedProcessIdentity : IApplicationProcessIdentity
    {
        public int ProcessId => 42;

        public DateTimeOffset StartedAtUtc => new(2026, 8, 5, 11, 59, 0, TimeSpan.Zero);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }


    private sealed class NeverStartedUpdaterHost : IUpdaterHostStarter
    {
        public void Start(string hostPath, string requestOption, string requestPath, string nonce) =>
            throw new AssertFailedException("The portable updater host must not start for packaged applications.");
    }

    private sealed class NeverTrustedSignatureVerifier : IExecutableSignatureVerifier
    {
        public Task<ExecutableSignatureResult> VerifyAsync(
            string filePath,
            IReadOnlySet<string> allowedPublisherThumbprints,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ExecutableSignatureResult(false, null, null, "signature.not_called"));
    }

    private sealed class RejectingFailureSink : IApplicationFailureSink
    {
        public void Report(string safeCode, Exception exception, Guid? profileId = null) =>
            throw new AssertFailedException($"Unexpected update failure: {safeCode}");
    }

    private sealed class InMemorySettingsStore : ISettingsStore
    {
        public Task<SettingsValidationResult> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(SettingsValidation.Normalize(new AppSettings()));

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
