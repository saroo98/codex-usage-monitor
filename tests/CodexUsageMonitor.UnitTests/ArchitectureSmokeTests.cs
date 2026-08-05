using CodexUsageMonitor.Core.Usage;

namespace CodexUsageMonitor.UnitTests;

[TestClass]
public sealed class ArchitectureSmokeTests
{
    [TestMethod]
    public void CoreAssemblyLoads() => Assert.AreEqual(0m, UsageMath.NormalizeUsedPercent(0m, null));
}
