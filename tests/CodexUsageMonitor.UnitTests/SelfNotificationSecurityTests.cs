using System.Reflection;
using CodexUsageMonitor.Email.Models;
using CodexUsageMonitor.Email.Security;

namespace CodexUsageMonitor.UnitTests;

[TestClass]
public sealed class SelfNotificationSecurityTests
{
    [TestMethod]
    public void SendingBoundaryDerivesTheOnlyRecipientFromTheAccountIdentity()
    {
        var account = EmailAccountIdentity.Create("person@example.com");
        var notification = new SelfNotification(
            "Codex usage warning",
            "Your allowance is running low.",
            null,
            "usage:warning");

        var message = SelfOnlyMessageFactory.Create(account, notification);

        Assert.AreEqual("person@example.com", message.AccountAddress);
        Assert.AreEqual(message.AccountAddress, message.FromAddress);
        Assert.AreEqual(message.AccountAddress, message.ToAddress);
    }

    [TestMethod]
    public void PublicSendingApiHasNoRecipientCcOrBccInput()
    {
        var method = typeof(ISelfNotificationSender).GetMethod(nameof(ISelfNotificationSender.SendSelfNotificationAsync))
            ?? throw new AssertFailedException("Self-only sending method is missing.");
        var forbidden = new[] { "to", "recipient", "cc", "bcc" };

        Assert.IsFalse(
            method.GetParameters().Any(parameter => forbidden.Any(value =>
                string.Equals(parameter.Name, value, StringComparison.OrdinalIgnoreCase) ||
                parameter.Name?.EndsWith(value, StringComparison.OrdinalIgnoreCase) is true)),
            "The public sending boundary must not accept recipient, Cc, or Bcc parameters.");
        Assert.IsFalse(
            typeof(SelfNotification).GetProperties(BindingFlags.Instance | BindingFlags.Public).Any(property => forbidden.Any(value =>
                property.Name.Contains(value, StringComparison.OrdinalIgnoreCase))),
            "Notification content must not contain addressing fields.");
        Assert.IsNull(typeof(ISelfNotificationSender).Assembly.GetType("CodexUsageMonitor.Email.Models.EmailMessage"));
        Assert.IsNull(typeof(ISelfNotificationSender).Assembly.GetType("CodexUsageMonitor.Email.Models.IEmailTransport"));
    }

    [TestMethod]
    [DataRow("victim@example.com\r\nBcc: attacker@example.com", "Subject", "event")]
    [DataRow("person@example.com", "Subject\r\nBcc: attacker@example.com", "event")]
    [DataRow("person@example.com", "Subject", "event\r\nTo: attacker@example.com")]
    public void HeaderInjectionIsRejected(string address, string subject, string deduplicationKey)
    {
        Assert.Throws<ArgumentException>(() =>
        {
            var account = EmailAccountIdentity.Create(address);
            var notification = new SelfNotification(subject, "Body", null, deduplicationKey);
            _ = SelfOnlyMessageFactory.Create(account, notification);
        });
    }
}
