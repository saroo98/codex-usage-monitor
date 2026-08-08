using CodexUsageMonitor.App.ViewModels;
using CodexUsageMonitor.Core.Settings;

namespace CodexUsageMonitor.UiTests;

[TestClass]
public sealed class EmailSettingsPresentationTests
{
    [TestMethod]
    public void ProviderChoicesUseProductNamesOnly()
    {
        var section = new EmailSettingsSectionViewModel();

        CollectionAssert.AreEqual(
            new[] { "Gmail", "Outlook / Microsoft 365", "Proton Mail", "Other email (SMTP) [Advanced]", "Off" },
            section.Providers.Select(choice => choice.DisplayName).ToArray());
        Assert.IsFalse(section.Providers.Any(choice =>
            choice.DisplayName.Contains("OAuth", StringComparison.OrdinalIgnoreCase) ||
            choice.DisplayName.Contains("GenericSmtp", StringComparison.OrdinalIgnoreCase) ||
            choice.DisplayName.Contains("Disabled", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void OAuthConnectionNeedsOnlyAnAvailableProviderRegistration()
    {
        var section = new EmailSettingsSectionViewModel
        {
            Provider = EmailProviderMode.Gmail,
            GoogleConnectionAvailable = true,
        };

        Assert.IsTrue(section.CanChangeOAuthConnection);
        Assert.IsFalse(section.Enabled);
        Assert.IsFalse(section.IsValid, "An OAuth provider is not ready for notifications until it is connected.");

        section.SetConnectedAddress("person@example.com");
        Assert.IsTrue(section.IsValid);
        Assert.AreEqual("Connected as person@example.com", section.ConnectedAsText);
    }

    [TestMethod]
    public void OffAlwaysDisablesEmailAndSmtpRejectsPlaintext()
    {
        var current = new EmailSettings { Provider = EmailProviderMode.Gmail, Enabled = true, ConnectedAddress = "person@example.com" };
        var section = new EmailSettingsSectionViewModel();
        section.Load(current);
        section.Provider = EmailProviderMode.Off;

        Assert.IsFalse(section.ApplyTo(current, keepSmtpCredential: false, keepOAuthTokens: false).Enabled);

        section.Provider = EmailProviderMode.OtherSmtp;
        section.SenderAddress = "person@example.com";
        section.SmtpHost = "smtp.example.com";
        section.SmtpSecurity = SmtpSecurityMode.None;
        Assert.IsFalse(section.IsValid);
    }

    [TestMethod]
    public void ProtonDefaultsToLocalBridgeAndUsesSelfAddress()
    {
        var section = new EmailSettingsSectionViewModel { Provider = EmailProviderMode.ProtonMailBridge };
        section.SenderAddress = "bridge-user@example.com";
        section.SmtpUsername = "bridge-generated-user";

        Assert.AreEqual("127.0.0.1", section.SmtpHost);
        Assert.AreEqual(1025, section.SmtpPort);
        Assert.AreEqual(SmtpSecurityMode.StartTls, section.SmtpSecurity);
        Assert.IsTrue(section.IsValid);
        var settings = section.ApplyTo(new EmailSettings(), keepSmtpCredential: false, keepOAuthTokens: false);
        Assert.AreEqual("bridge-user@example.com", settings.SenderAddress);
        Assert.AreEqual(0, settings.Recipients.Count);
    }
}
