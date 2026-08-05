using CodexUsageMonitor.Application.Runtime;
using CodexUsageMonitor.Core.Abstractions;

namespace CodexUsageMonitor.UnitTests;

[TestClass]
public sealed class StartupHealthQualificationTests
{
    [TestMethod]
    public async Task ReadyApplicationWithClassifiedCodexStateQualifiesImmediately()
    {
        var startup = CreateReadyStartup();
        var source = new FakeClassificationSource { IsClassified = true };
        var qualification = new StartupHealthQualification(startup, source, TimeSpan.FromSeconds(1));

        Assert.IsTrue(await qualification.WaitAsync(CancellationToken.None));
        Assert.AreEqual(0, source.SubscriptionCount);
    }

    [TestMethod]
    public async Task ClassificationSignalCompletesPendingQualification()
    {
        var startup = CreateReadyStartup();
        var source = new FakeClassificationSource();
        var qualification = new StartupHealthQualification(startup, source, TimeSpan.FromSeconds(1));

        var pending = qualification.WaitAsync(CancellationToken.None);
        Assert.AreEqual(1, source.SubscriptionCount);
        source.SetClassified();

        Assert.IsTrue(await pending);
        Assert.AreEqual(0, source.SubscriptionCount);
    }

    [TestMethod]
    public async Task ClassificationTimeoutWithholdsHealth()
    {
        var startup = CreateReadyStartup();
        var source = new FakeClassificationSource();
        var qualification = new StartupHealthQualification(startup, source, TimeSpan.FromMilliseconds(20));

        Assert.IsFalse(await qualification.WaitAsync(CancellationToken.None));
        Assert.AreEqual(0, source.SubscriptionCount);
    }

    [TestMethod]
    public async Task ApplicationThatIsNotReadyCannotQualify()
    {
        var startup = new ApplicationStartupState(new FixedClock());
        startup.Begin();
        var source = new FakeClassificationSource { IsClassified = true };
        var qualification = new StartupHealthQualification(startup, source, TimeSpan.FromSeconds(1));

        Assert.IsFalse(await qualification.WaitAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task CallerCancellationIsPropagatedAndSubscriptionIsRemoved()
    {
        var startup = CreateReadyStartup();
        var source = new FakeClassificationSource();
        var qualification = new StartupHealthQualification(startup, source, TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource();

        var pending = qualification.WaitAsync(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        Assert.AreEqual(0, source.SubscriptionCount);
    }

    private static ApplicationStartupState CreateReadyStartup()
    {
        var startup = new ApplicationStartupState(new FixedClock());
        startup.Begin();
        startup.Advance(ApplicationStartupStage.Ready);
        return startup;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class FakeClassificationSource : IStartupHealthClassificationSource
    {
        private EventHandler? _classificationChanged;

        public event EventHandler? ClassificationChanged
        {
            add
            {
                _classificationChanged += value;
                SubscriptionCount++;
            }
            remove
            {
                _classificationChanged -= value;
                SubscriptionCount--;
            }
        }

        public bool IsClassified { get; set; }

        public int SubscriptionCount { get; private set; }

        public void SetClassified()
        {
            IsClassified = true;
            _classificationChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
