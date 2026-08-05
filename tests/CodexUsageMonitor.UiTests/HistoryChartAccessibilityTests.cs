using CodexUsageMonitor.App.Controls;
using CodexUsageMonitor.Persistence.History;

namespace CodexUsageMonitor.UiTests;

[TestClass]
public sealed class HistoryChartAccessibilityTests
{
    [TestMethod]
    public void AccessibleSummaryExplainsEmptyHistory()
    {
        Assert.AreEqual("No confirmed usage history is available.", HistoryChart.Summarize([]));
    }

    [TestMethod]
    public void AccessibleSummaryReportsRangeAndTrendWithoutAccountData()
    {
        var summary = HistoryChart.Summarize(
        [
            new HistoryPoint(new DateTimeOffset(2026, 8, 5, 9, 0, 0, TimeSpan.Zero), 80m, 20m, null),
            new HistoryPoint(new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero), 63.5m, 36.5m, null),
        ]);

        StringAssert.Contains(summary, "2 confirmed samples");
        StringAssert.Contains(summary, "decreased from 80% to 63.5%");
        Assert.IsFalse(summary.Contains("account", StringComparison.OrdinalIgnoreCase));
    }
}
