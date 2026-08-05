using CodexUsageMonitor.Core.Scheduling;

namespace CodexUsageMonitor.PerformanceTests;

[TestClass]
public sealed class ArchitectureSmokeTests
{
    [TestMethod]
    public void SchedulerTypeIsAvailable() => Assert.IsNotNull(typeof(AdaptiveUiScheduler));
}
