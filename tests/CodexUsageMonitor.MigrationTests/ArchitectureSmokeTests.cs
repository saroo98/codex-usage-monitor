using CodexUsageMonitor.Migration.Legacy;

namespace CodexUsageMonitor.MigrationTests;

[TestClass]
public sealed class ArchitectureSmokeTests
{
    [TestMethod]
    public void LegacyMapperCanBeConstructed() => Assert.IsNotNull(new LegacySettingsMapper());
}
