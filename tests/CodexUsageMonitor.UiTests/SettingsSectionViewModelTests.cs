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

    [TestMethod]
    public void EmailSectionExposesProviderSpecificValidationAndCommandState()
    {
        var section = new EmailSettingsSectionViewModel
        {
            Provider = EmailProviderMode.MicrosoftOAuth,
            SenderAddress = "person@example.com",
            RecipientAddress = "alerts@example.com",
        };

        Assert.IsFalse(section.IsValid);
        Assert.IsFalse(section.CanChangeOAuthConnection);
        StringAssert.Contains(section.ValidationMessage, "client ID");

        section.OAuthClientId = "registered-client";
        Assert.IsTrue(section.IsValid);
        Assert.IsTrue(section.CanChangeOAuthConnection);

        section.OAuthBusy = true;
        Assert.IsFalse(section.CanChangeOAuthConnection);
    }

    [TestMethod]
    public void EmailSectionRejectsInvalidEnvelopeBeforeCredentialOperations()
    {
        var section = new EmailSettingsSectionViewModel
        {
            Provider = EmailProviderMode.GenericSmtp,
            SenderAddress = "not-an-address",
            RecipientAddress = "alerts@example.com",
            SmtpHost = "smtp.example.com",
        };

        Assert.IsFalse(section.IsValid);
        StringAssert.Contains(section.ValidationMessage, "sender email address");

        section.SenderAddress = "person@example.com";
        section.RecipientAddress = "also-not-an-address";
        Assert.IsFalse(section.IsValid);
        StringAssert.Contains(section.ValidationMessage, "recipient email addresses");
    }

    [TestMethod]
    public void OAuthConnectionReadinessDoesNotRequireNotificationRecipients()
    {
        var section = new EmailSettingsSectionViewModel
        {
            Provider = EmailProviderMode.GoogleOAuth,
            SenderAddress = "person@example.com",
            OAuthClientId = "registered-client",
        };

        Assert.IsFalse(section.IsValid);
        Assert.IsTrue(section.CanChangeOAuthConnection);
    }
}
