using CodexUsageMonitor.Core.Settings;

namespace CodexUsageMonitor.IntegrationTests;

[TestClass]
public sealed class ArchitectureSmokeTests
{
    [TestMethod]
    public void SettingsNormalizeFromNull() => Assert.IsNotNull(SettingsValidation.Normalize(null).Settings);
}
