namespace CodexUsageMonitor.UiTests;

[TestClass]
public sealed class WindowsAccessibilityContractTests
{
    [TestMethod]
    public void ThemesProvideVisibleFocusAndSystemHighContrastTokens()
    {
        var controls = ReadSource("src", "CodexUsageMonitor.App", "Themes", "Controls.xaml");
        var highContrast = ReadSource("src", "CodexUsageMonitor.App", "Themes", "HighContrast.xaml");

        StringAssert.Contains(controls, "KeyboardFocusVisualStyle");
        StringAssert.Contains(controls, "BorderThickness=\"2\"");
        StringAssert.Contains(controls, "RecognizesAccessKey=\"True\"");
        StringAssert.Contains(highContrast, "SystemColors.HighlightTextColorKey");
        StringAssert.Contains(highContrast, "SystemColors.HotTrackColorKey");
    }

    [TestMethod]
    public void PrimaryWindowsExposeHeadingsLabelsLiveRegionsAndAdaptiveScrolling()
    {
        var settings = ReadSource("src", "CodexUsageMonitor.App", "Views", "SettingsWindow.xaml");
        var onboarding = ReadSource("src", "CodexUsageMonitor.App", "Views", "OnboardingWindow.xaml");
        var reset = ReadSource("src", "CodexUsageMonitor.App", "Views", "ResetCreditConfirmationDialog.xaml");
        var oauth = ReadSource("src", "CodexUsageMonitor.App", "Views", "MicrosoftDeviceCodeDialog.xaml");
        var widget = ReadSource("src", "CodexUsageMonitor.App", "Views", "WidgetWindow.xaml");

        foreach (var accessibleName in new[]
        {
            "Display language",
            "Widget size",
            "Notification thresholds",
            "Email provider",
            "Codex profiles",
            "History retention days",
            "Update channel",
        })
        {
            StringAssert.Contains(settings, $"AutomationProperties.Name=\"{accessibleName}\"");
        }

        StringAssert.Contains(settings, "CompactNavigation");
        StringAssert.Contains(settings, "AutomationProperties.LiveSetting=\"Polite\"");
        StringAssert.Contains(onboarding, "ResizeMode=\"CanResizeWithGrip\"");
        StringAssert.Contains(onboarding, "AutomationProperties.HeadingLevel=\"Level1\"");
        StringAssert.Contains(reset, "VerticalScrollBarVisibility=\"Auto\"");
        StringAssert.Contains(oauth, "VerticalScrollBarVisibility=\"Auto\"");
        StringAssert.Contains(widget, "<KeyBinding Key=\"F5\"");
        StringAssert.Contains(widget, "AutomationProperties.LiveSetting=\"Polite\"");
        Assert.AreEqual(3, Count(widget, "Value=\"{Binding RemainingPercent, Mode=OneWay}\""));
    }

    private static string ReadSource(params string[] pathParts) =>
        File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. pathParts]));

    private static int Count(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;

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
