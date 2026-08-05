using CodexUsageMonitor.Application.Diagnostics;
using CodexUsageMonitor.Core.Diagnostics;

namespace CodexUsageMonitor.UnitTests;

[TestClass]
public sealed class DiagnosticSummaryFormatterTests
{
    [TestMethod]
    public void SummaryUsesSafeStableFieldsAndRedactsSensitiveValues()
    {
        var snapshot = new DiagnosticSnapshot(
            new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero),
            "1.0.0",
            "Windows 11",
            "X64",
            ".NET 10",
            false,
            true,
            "Live",
            new DateTimeOffset(2026, 8, 5, 11, 59, 0, TimeSpan.Zero),
            "codex 1.2.3",
            null,
            [new DiagnosticCheck("database.integrity", true, "ok")],
            new Dictionary<string, string>
            {
                ["logs.directory"] = @"C:\Users\private-user\AppData\Local\CodexUsageMonitor",
                ["email"] = "private@example.com",
            });

        var summary = DiagnosticSummaryFormatter.Format(snapshot);

        StringAssert.Contains(summary, "Codex Usage Monitor 1.0.0");
        StringAssert.Contains(summary, "PASS database.integrity: ok");
        StringAssert.Contains(summary, @"C:\Users\<user>\");
        StringAssert.Contains(summary, "<email>");
        Assert.IsFalse(summary.Contains("private-user", StringComparison.Ordinal));
        Assert.IsFalse(summary.Contains("private@example.com", StringComparison.Ordinal));
    }
}
