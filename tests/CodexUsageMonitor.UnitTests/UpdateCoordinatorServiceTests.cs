using CodexUsageMonitor.Application.Diagnostics;
using CodexUsageMonitor.Application.Settings;
using CodexUsageMonitor.Application.Updates;
using CodexUsageMonitor.Core.Abstractions;
using CodexUsageMonitor.Core.Settings;

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

    private static async Task<UpdateCoordinatorService> CreateServiceAsync(FakeUpdatePlatform platform)
    {
        var settings = new ApplicationSettingsService(new MemorySettingsStore(new AppSettings
        {
            Updates = new UpdateSettings { ManifestUri = new Uri("https://updates.example.test/manifest.json") },
        }));
        await settings.InitializeAsync(CancellationToken.None);
        return new UpdateCoordinatorService(
            platform,
            settings,
            new UpdateRuntimeState("6.0.0"),
            new FixedClock(),
            new RejectingFailureSink());
    }

    private sealed class FakeUpdatePlatform(TimeSpan checkDelay) : IUpdatePlatformPort
    {
        private int _activeChecks;

        public bool IsManagedExternally => false;

        public bool HasVerifiedCandidate { get; private set; }

        public bool HasPreparedUpdate { get; private set; }

        public int MaximumConcurrentChecks { get; private set; }

        public Action? PrepareStarted { get; set; }

        public async Task<UpdateCheckOutcome> CheckAsync(
            Uri manifestUri,
            string channel,
            string? entityTag,
            CancellationToken cancellationToken)
        {
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
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            HasPreparedUpdate = true;
        }

        public Task LaunchPreparedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
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
}
