using System.Reflection;
using CodexUsageMonitor.App.Runtime;
using CodexUsageMonitor.Core.Settings;
using CodexUsageMonitor.Windows.Runtime;

namespace CodexUsageMonitor.UiTests;

[TestClass]
public sealed class ApplicationBootstrapperLaunchTests
{
    [TestMethod]
    public void ExplicitShowWidgetOverridesLaunchMinimizedPreference()
    {
        var launch = AppCommandLine.Parse(["--show-widget"]);
        var settings = new AppSettings
        {
            General = new GeneralSettings { LaunchMinimized = true },
        };

        var normalized = NormalizeLaunch(launch, settings);

        Assert.AreEqual(ActivationCommandNames.ShowWidget, normalized.Commands.Single().Name);
    }

    [TestMethod]
    public void DefaultLaunchHonorsLaunchMinimizedPreference()
    {
        var launch = AppCommandLine.Parse([]);
        var settings = new AppSettings
        {
            General = new GeneralSettings { LaunchMinimized = true },
        };

        var normalized = NormalizeLaunch(launch, settings);

        Assert.AreEqual(ActivationCommandNames.HideWidget, normalized.Commands.Single().Name);
    }

    private static AppLaunchRequest NormalizeLaunch(AppLaunchRequest launch, AppSettings settings)
    {
        var method = typeof(ApplicationBootstrapper).GetMethod(
            "NormalizeLaunch",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method);
        return (AppLaunchRequest)method.Invoke(null, [launch, settings])!;
    }
}
