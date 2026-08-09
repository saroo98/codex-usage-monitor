using CodexUsageMonitor.App.ViewModels;
using CodexUsageMonitor.Core.Settings;

namespace CodexUsageMonitor.UiTests;

[TestClass]
public sealed class SettingsSectionViewModelTests
{
    [TestMethod]
    public void NotificationSectionOwnsThresholdAndQuietHoursValidation()
    {
        var section = new NotificationSettingsSectionViewModel();

        section.Thresholds = "20, invalid";
        Assert.IsFalse(section.IsValid);
        StringAssert.Contains(section.ValidationMessage, "0 to 100");

        section.Thresholds = "20, 10, 0";
        section.QuietHoursEnabled = true;
        section.QuietHoursStart = "9pm";
        Assert.IsFalse(section.IsValid);
        StringAssert.Contains(section.ValidationMessage, "HH:mm");

        section.QuietHoursStart = "21:00";
        section.QuietHoursEnd = "08:00";
        Assert.IsTrue(section.IsValid);
        Assert.IsNull(section.ValidationMessage);
    }

}
