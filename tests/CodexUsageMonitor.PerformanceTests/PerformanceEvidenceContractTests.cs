namespace CodexUsageMonitor.PerformanceTests;

[TestClass]
public sealed class PerformanceEvidenceContractTests
{
    [TestMethod]
    public void ReleaseMeasurementIsolatedFixtureEnforcesDeclaredBudgets()
    {
        var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "eng", "measure-performance.ps1"));

        StringAssert.Contains(script, "[int]$ColdStartIterations = 3");
        StringAssert.Contains(script, "coldStartMilliseconds = 2000");
        StringAssert.Contains(script, "idleCpuP95Percent = 1");
        StringAssert.Contains(script, "privateWorkingSetMiB = 150");
        StringAssert.Contains(script, "soakGrowthPercent = 10");
        StringAssert.Contains(script, "leakedChildProcesses = 0");
        StringAssert.Contains(script, "showOnboardingOnNextLaunch = $false");
        StringAssert.Contains(script, "enabled = $false");
        StringAssert.Contains(script, "$process.Kill($true)");
        StringAssert.Contains(script, "Refusing to terminate a process outside the isolated performance fixture");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CodexUsageMonitor.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
