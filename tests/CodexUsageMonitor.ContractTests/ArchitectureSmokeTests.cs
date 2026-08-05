using CodexUsageMonitor.Codex.Mapping;
using CodexUsageMonitor.Core.Diagnostics;

namespace CodexUsageMonitor.ContractTests;

[TestClass]
public sealed class ArchitectureSmokeTests
{
    [TestMethod]
    public void ContractMapperCanBeConstructed() => Assert.IsNotNull(new CodexSnapshotMapper(NullProtocolAnomalySink.Instance));
}
