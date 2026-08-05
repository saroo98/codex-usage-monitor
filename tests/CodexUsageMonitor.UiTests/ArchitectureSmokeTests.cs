using CodexUsageMonitor.App.Views;

namespace CodexUsageMonitor.UiTests;

[TestClass]
public sealed class ArchitectureSmokeTests
{
    [TestMethod]
    public void WidgetWindowTypeIsAvailable() => Assert.IsNotNull(typeof(WidgetWindow));
}
