using CodexUsageMonitor.Application.Settings;
using CodexUsageMonitor.Core.Settings;

namespace CodexUsageMonitor.UnitTests;

[TestClass]
public sealed class ApplicationSettingsServiceTests
{
    [TestMethod]
    public async Task ConcurrentUpdatesAreSerializedAgainstLatestState()
    {
        var store = new MemorySettingsStore(new AppSettings(), TimeSpan.FromMilliseconds(20));
        var settings = new ApplicationSettingsService(store);
        await settings.InitializeAsync(CancellationToken.None);

        await Task.WhenAll(
            settings.UpdateAsync(current => current with
            {
                General = current.General with { StartWithWindows = true },
            }, CancellationToken.None),
            settings.UpdateAsync(current => current with
            {
                General = current.General with { LaunchMinimized = true },
            }, CancellationToken.None));

        Assert.IsTrue(settings.Current.General.StartWithWindows);
        Assert.IsTrue(settings.Current.General.LaunchMinimized);
        Assert.AreEqual(1, store.MaximumConcurrentSaves);
    }

    [TestMethod]
    public async Task CancelledUpdateLeavesConfirmedSettingsUnchanged()
    {
        var initial = new AppSettings();
        var settings = new ApplicationSettingsService(new MemorySettingsStore(initial, TimeSpan.Zero));
        await settings.InitializeAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => settings.UpdateAsync(
            current => current with { General = current.General with { StartWithWindows = true } },
            cancellation.Token));

        Assert.IsFalse(settings.Current.General.StartWithWindows);
    }

    private sealed class MemorySettingsStore(AppSettings initial, TimeSpan saveDelay) : ISettingsStore
    {
        private AppSettings _current = initial;
        private int _activeSaves;

        public int MaximumConcurrentSaves { get; private set; }

        public Task<SettingsValidationResult> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(SettingsValidation.Normalize(_current));

        public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _activeSaves);
            MaximumConcurrentSaves = Math.Max(MaximumConcurrentSaves, active);
            try
            {
                await Task.Delay(saveDelay, cancellationToken);
                _current = settings;
            }
            finally
            {
                Interlocked.Decrement(ref _activeSaves);
            }
        }
    }
}
