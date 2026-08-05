using CodexUsageMonitor.Core.Scheduling;
using CodexUsageMonitor.Email.Outbox;

namespace CodexUsageMonitor.UnitTests;

[TestClass]
public sealed class EmailRetryBackoffPolicyTests
{
    [TestMethod]
    public void DelayUsesBoundedFullJitter()
    {
        var minimum = new EmailRetryBackoffPolicy(new FixedRandom(0d));
        var maximum = new EmailRetryBackoffPolicy(new FixedRandom(1d));

        Assert.AreEqual(TimeSpan.FromSeconds(1), minimum.DelayForAttempt(1));
        Assert.AreEqual(TimeSpan.FromSeconds(15), maximum.DelayForAttempt(1));
        Assert.AreEqual(TimeSpan.FromHours(1), maximum.DelayForAttempt(20));
    }

    [TestMethod]
    public void DelayRejectsNonPositiveAttempts()
    {
        var policy = new EmailRetryBackoffPolicy(new FixedRandom(0.5d));

        Assert.Throws<ArgumentOutOfRangeException>(() => policy.DelayForAttempt(0));
    }

    private sealed class FixedRandom(double value) : IRandomSource
    {
        public double NextDouble() => value;
    }
}
