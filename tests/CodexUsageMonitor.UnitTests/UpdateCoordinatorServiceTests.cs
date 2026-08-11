using System.IO.Compression;
using CodexUsageMonitor.Application.Diagnostics;
using CodexUsageMonitor.Application.Settings;
using CodexUsageMonitor.Application.Updates;
using CodexUsageMonitor.Core.Abstractions;
using CodexUsageMonitor.Core.Diagnostics;
using CodexUsageMonitor.Core.Settings;
using CodexUsageMonitor.Persistence.Database;
using CodexUsageMonitor.Persistence.Diagnostics;
using CodexUsageMonitor.Persistence.Paths;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexUsageMonitor.UnitTests;

[TestClass]
public sealed class UpdateCoordinatorServiceTests
{
    [TestMethod]
    public async Task ConcurrentChecksAreSerialized()
    {
        var platform = new FakeUpdatePlatform(TimeSpan.FromMilliseconds(30));
        var service = await CreateServiceAsync(platform);

        await Task.WhenAll(
            service.CheckAsync(manual: true, CancellationToken.None),
            service.CheckAsync(manual: true, CancellationToken.None));

        Assert.AreEqual(1, platform.MaximumConcurrentChecks);
    }

    [TestMethod]
    public async Task CancelledPreparationRestoresAvailableState()
    {
        var platform = new FakeUpdatePlatform(TimeSpan.Zero);
        var service = await CreateServiceAsync(platform);
        await service.CheckAsync(manual: true, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        platform.PrepareStarted = cancellation.Cancel;

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.PrepareAsync(cancellation.Token));

        Assert.AreEqual(UpdateRuntimeStatus.Available, service.Current.Status);
        Assert.IsTrue(service.Current.CanPrepare);
        Assert.IsFalse(service.Current.CanInstall);
    }

    [TestMethod]
    [DataRow("check")]
    [DataRow("prepare")]
    [DataRow("install")]
    public async Task ExpectedUpdateFailuresReportOnlySafeCodeAndExceptionCategory(string operation)
    {
        const string privateUri = "https://private-feed.example.test:8443/customer-92831/releases/update-manifest.json?tenant=private-tenant-773";
        var rawFailure = new HttpRequestException(
            $"Request to {privateUri} failed.",
            new IOException($"Download endpoint {privateUri} was unavailable."));
        var platform = new FakeUpdatePlatform(TimeSpan.Zero);
        var sink = new RecordingFailureSink();
        var service = await CreateServiceAsync(platform, sink, new Uri(privateUri));

        UpdateRuntimeSnapshot result;
        switch (operation)
        {
            case "check":
                platform.CheckFailure = rawFailure;
                result = await service.CheckAsync(manual: true, CancellationToken.None);
                break;
            case "prepare":
                await service.CheckAsync(manual: true, CancellationToken.None);
                platform.PrepareFailure = rawFailure;
                result = await service.PrepareAsync(CancellationToken.None);
                break;
            case "install":
                await service.CheckAsync(manual: true, CancellationToken.None);
                await service.PrepareAsync(CancellationToken.None);
                platform.InstallFailure = rawFailure;
                result = await service.InstallPreparedAsync(CancellationToken.None);
                break;
            default:
                throw new AssertFailedException($"Unknown operation fixture: {operation}");
        }

        Assert.AreEqual(UpdateRuntimeStatus.Failed, result.Status);
        Assert.AreEqual("update.network_failed", result.SafeErrorCode);
        Assert.AreEqual(1, sink.Reports.Count);
        Assert.AreEqual("update.network_failed", sink.Reports[0].SafeCode);
        Assert.AreNotSame(rawFailure, sink.Reports[0].Exception);
        Assert.AreEqual(nameof(HttpRequestException), sink.Reports[0].Exception.Message);
        Assert.IsNull(sink.Reports[0].Exception.InnerException);
        Assert.IsFalse(sink.Reports[0].Exception.ToString().Contains(privateUri, StringComparison.Ordinal));
        Assert.IsFalse(sink.Reports[0].Exception.ToString().Contains("private-feed.example.test", StringComparison.Ordinal));
        Assert.IsFalse(sink.Reports[0].Exception.ToString().Contains("customer-92831", StringComparison.Ordinal));
        Assert.IsFalse(sink.Reports[0].Exception.ToString().Contains("private-tenant-773", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ExpectedUpdateFailureEndpointDataDoesNotReachSupportBundleLogs()
    {
        const string privateUri = "https://private-feed.example.test:8443/customer-92831/releases/update-manifest.json?tenant=private-tenant-773";
        var root = Path.Combine(Path.GetTempPath(), "cum-update-log-privacy", Guid.NewGuid().ToString("N"));
        var paths = new AppDataPaths(
            root,
            Path.Combine(root, "settings.json"),
            Path.Combine(root, "usage.db"),
            Path.Combine(root, "logs"),
            Path.Combine(root, "cache"),
            Path.Combine(root, "updates"),
            Path.Combine(root, "support"),
            IsPortable: true);
        paths.EnsureCreated();
        try
        {
            var provider = new RedactingFileLoggerProvider(paths.LogsDirectory);
            try
            {
                var platform = new FakeUpdatePlatform(TimeSpan.Zero)
                {
                    CheckFailure = new HttpRequestException(
                        $"Request to {privateUri} failed.",
                        new IOException($"Inner download failure for {privateUri}.")),
                };
                var sink = new LoggingFailureSink(provider.CreateLogger("UpdatePrivacyRegression"));
                var service = await CreateServiceAsync(platform, sink, new Uri(privateUri));

                var result = await service.CheckAsync(manual: true, CancellationToken.None);

                Assert.AreEqual(UpdateRuntimeStatus.Failed, result.Status);
                Assert.AreEqual("update.network_failed", result.SafeErrorCode);
            }
            finally
            {
                await provider.DisposeAsync();
            }

            var database = new UsageDatabase(
                new SqliteConnectionFactory(paths.DatabaseFile),
                NullLogger<UsageDatabase>.Instance);
            var builder = new SupportBundleBuilder(paths, database);
            var bundlePath = Path.Combine(paths.SupportDirectory, "support.zip");
            var snapshot = new DiagnosticSnapshot(
                new DateTimeOffset(2026, 8, 10, 18, 30, 0, TimeSpan.Zero),
                "6.0.0",
                "Windows",
                "X64",
                ".NET 10",
                false,
                true,
                "Live",
                null,
                null,
                "update.network_failed",
                []);
            await builder.BuildAsync(bundlePath, snapshot, new AppSettings
            {
                Updates = new UpdateSettings { ManifestUri = new Uri(privateUri) },
            }, CancellationToken.None);

            using var archive = ZipFile.OpenRead(bundlePath);
            Assert.IsTrue(archive.Entries.Any(entry => entry.FullName.StartsWith("logs/", StringComparison.Ordinal)));
            var content = new List<string>();
            foreach (var entry in archive.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                content.Add(await reader.ReadToEndAsync(CancellationToken.None));
            }

            var allContent = string.Join('\n', content);
            Assert.IsFalse(allContent.Contains(privateUri, StringComparison.Ordinal));
            Assert.IsFalse(allContent.Contains("private-feed.example.test:8443", StringComparison.Ordinal));
            Assert.IsFalse(allContent.Contains("customer-92831/releases/update-manifest.json", StringComparison.Ordinal));
            Assert.IsFalse(allContent.Contains("private-tenant-773", StringComparison.Ordinal));
            StringAssert.Contains(allContent, "update.network_failed");
            StringAssert.Contains(allContent, nameof(HttpRequestException));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<UpdateCoordinatorService> CreateServiceAsync(
        FakeUpdatePlatform platform,
        IApplicationFailureSink? failureSink = null,
        Uri? manifestUri = null)
    {
        var settings = new ApplicationSettingsService(new MemorySettingsStore(new AppSettings
        {
            Updates = new UpdateSettings { ManifestUri = manifestUri ?? new Uri("https://updates.example.test/manifest.json") },
        }));
        await settings.InitializeAsync(CancellationToken.None);
        return new UpdateCoordinatorService(
            platform,
            settings,
            new UpdateRuntimeState("6.0.0"),
            new FixedClock(),
            failureSink ?? new RejectingFailureSink());
    }

    private sealed class FakeUpdatePlatform(TimeSpan checkDelay) : IUpdatePlatformPort
    {
        private int _activeChecks;

        public bool IsManagedExternally => false;

        public bool HasVerifiedCandidate { get; private set; }

        public bool HasPreparedUpdate { get; private set; }

        public int MaximumConcurrentChecks { get; private set; }

        public Action? PrepareStarted { get; set; }

        public Exception? CheckFailure { get; set; }

        public Exception? PrepareFailure { get; set; }

        public Exception? InstallFailure { get; set; }

        public async Task<UpdateCheckOutcome> CheckAsync(
            Uri manifestUri,
            string channel,
            string? entityTag,
            CancellationToken cancellationToken)
        {
            if (CheckFailure is not null)
            {
                throw CheckFailure;
            }

            var active = Interlocked.Increment(ref _activeChecks);
            MaximumConcurrentChecks = Math.Max(MaximumConcurrentChecks, active);
            try
            {
                await Task.Delay(checkDelay, cancellationToken);
                HasVerifiedCandidate = true;
                return new UpdateCheckOutcome(
                    UpdateCheckOutcomeStatus.Available,
                    "6.0.1",
                    null,
                    "etag",
                    true);
            }
            finally
            {
                Interlocked.Decrement(ref _activeChecks);
            }
        }

        public async Task PrepareAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            PrepareStarted?.Invoke();
            if (PrepareFailure is not null)
            {
                throw PrepareFailure;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            HasPreparedUpdate = true;
        }

        public Task LaunchPreparedAsync(CancellationToken cancellationToken) =>
            InstallFailure is null ? Task.CompletedTask : Task.FromException(InstallFailure);
    }

    private sealed class MemorySettingsStore(AppSettings settings) : ISettingsStore
    {
        public Task<SettingsValidationResult> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(SettingsValidation.Normalize(settings));

        public Task SaveAsync(AppSettings value, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class RejectingFailureSink : IApplicationFailureSink
    {
        public void Report(string safeCode, Exception exception, Guid? profileId = null) =>
            throw new AssertFailedException($"Unexpected update failure: {safeCode}");
    }

    private sealed class RecordingFailureSink : IApplicationFailureSink
    {
        public List<(string SafeCode, Exception Exception)> Reports { get; } = [];

        public void Report(string safeCode, Exception exception, Guid? profileId = null) =>
            Reports.Add((safeCode, exception));
    }

    private sealed class LoggingFailureSink(ILogger logger) : IApplicationFailureSink
    {
        private readonly ILogger _logger = logger;

        public void Report(string safeCode, Exception exception, Guid? profileId = null) =>
            _logger.LogWarning(
                exception,
                "Application operation {SafeCode} failed for profile {ProfileId}.",
                safeCode,
                profileId);
    }
}
