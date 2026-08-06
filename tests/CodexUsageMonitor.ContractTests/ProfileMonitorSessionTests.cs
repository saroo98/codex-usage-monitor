using System.Text.Json;
using System.Threading.Channels;
using CodexUsageMonitor.Application.Monitoring;
using CodexUsageMonitor.Codex;
using CodexUsageMonitor.Codex.Contracts;
using CodexUsageMonitor.Codex.Mapping;
using CodexUsageMonitor.Codex.Monitoring;
using CodexUsageMonitor.Core.Abstractions;
using CodexUsageMonitor.Core.Diagnostics;
using CodexUsageMonitor.Core.Monitoring;
using CodexUsageMonitor.Core.Profiles;
using CodexUsageMonitor.Core.Usage;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexUsageMonitor.ContractTests;

[TestClass]
public sealed class ProfileMonitorSessionTests
{
    [TestMethod]
    public async Task PushUpdateDoesNotStopPeriodicAndManualRefreshProcessing()
    {
        var client = new RecordingAppServerClient();
        var callbacks = new RecordingCallbacks();
        var profile = ProfileDefinition.CreateDefault();
        await using var session = new ProfileMonitorSession(
            profile,
            client,
            new CodexSnapshotMapper(NullProtocolAnomalySink.Instance),
            new SparseRateLimitsMerger(),
            new UsageStateReducer(new SnapshotCoherenceValidator(NullProtocolAnomalySink.Instance)),
            callbacks,
            new SystemClock(),
            NullLogger<ProfileMonitorSession>.Instance);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var run = session.RunAsync(lifetime.Token);
        _ = await callbacks.Snapshots.Reader.ReadAsync(lifetime.Token);

        client.PublishRateLimits(61);
        _ = await callbacks.Snapshots.Reader.ReadAsync(lifetime.Token);
        Assert.IsTrue(session.RequestRefresh(manual: true));

        var refreshed = callbacks.Snapshots.Reader.ReadAsync(lifetime.Token).AsTask();
        var completed = await Task.WhenAny(run, refreshed);
        Assert.AreSame(
            refreshed,
            completed,
            $"Monitor session stopped after a push update: {run.Exception?.GetBaseException().Message}");
        _ = await refreshed;

        lifetime.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => run);
    }

    private sealed class RecordingAppServerClient : IAppServerClient
    {
        public bool IsInitialized { get; private set; }

        public event EventHandler<RateLimitsReadResult>? RateLimitsUpdated;

        public Task<AppServerInitialization> InitializeAsync(CancellationToken cancellationToken)
        {
            IsInitialized = true;
            return Task.FromResult(new AppServerInitialization(null, "windows", "test", Json("{}")));
        }

        public Task<AccountReadResult> ReadAccountAsync(bool refreshToken, CancellationToken cancellationToken) =>
            Task.FromResult(new AccountReadResult(Json("""{"account":{"id":"synthetic-account"}}""")));

        public Task<RateLimitsReadResult> ReadRateLimitsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new RateLimitsReadResult(RateLimits(62)));

        public Task<UsageReadResult> ReadUsageAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new UsageReadResult(false, Json("{}")));

        public Task<ResetCreditConsumeResult> ConsumeResetCreditAsync(
            string? creditId,
            Guid idempotencyKey,
            CancellationToken cancellationToken) =>
            Task.FromResult(ResetCreditConsumeResult.Rejected("unsupported"));

        public void PublishRateLimits(int remainingPercent) =>
            RateLimitsUpdated?.Invoke(this, new RateLimitsReadResult(RateLimits(remainingPercent)));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static JsonElement RateLimits(int remainingPercent) =>
            JsonSerializer.SerializeToElement(new
            {
                rateLimits = new
                {
                    primary = new
                    {
                        remainingPercent = remainingPercent.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        windowDurationMins = 300,
                    },
                },
            });

        private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
    }

    private sealed class RecordingCallbacks : IProfileMonitorCallbacks
    {
        public Channel<UsageSnapshot> Snapshots { get; } = Channel.CreateUnbounded<UsageSnapshot>();

        public ValueTask SnapshotReceivedAsync(
            ProfileDefinition profile,
            UsageSnapshot snapshot,
            CancellationToken cancellationToken) =>
            Snapshots.Writer.WriteAsync(snapshot, cancellationToken);

        public ValueTask StateChangedAsync(
            ProfileDefinition profile,
            MonitorState state,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class SystemClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;
    }
}
