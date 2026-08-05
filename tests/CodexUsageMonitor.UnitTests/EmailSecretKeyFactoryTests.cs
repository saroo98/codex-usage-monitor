using CodexUsageMonitor.Core.Settings;
using CodexUsageMonitor.Email.Security;

namespace CodexUsageMonitor.UnitTests;

[TestClass]
public sealed class EmailSecretKeyFactoryTests
{
    [TestMethod]
    public void SmtpReferenceDoesNotExposeSenderAddress()
    {
        var reference = EmailSecretKeyFactory.SmtpPassword("Person.Example@example.com");
        Assert.IsFalse(reference.Contains("Person", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(reference.Contains("example.com", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void OAuthReferenceChangesWithProviderRegistration()
    {
        var first = EmailSecretKeyFactory.OAuthTokens(
            EmailProviderMode.GoogleOAuth,
            "person@example.com",
            "client-one");
        var second = EmailSecretKeyFactory.OAuthTokens(
            EmailProviderMode.GoogleOAuth,
            "person@example.com",
            "client-two");

        Assert.AreNotEqual(first, second);
        Assert.AreEqual(first, EmailSecretKeyFactory.OAuthTokens(
            EmailProviderMode.GoogleOAuth,
            "PERSON@example.com",
            "client-one"));
    }
}
