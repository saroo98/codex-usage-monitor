using CodexUsageMonitor.Email.Models;

namespace CodexUsageMonitor.Email.Security;

public static class SelfOnlyMessageFactory
{
    public static SelfEmailMessage Create(EmailAccountIdentity account, SelfNotification notification)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(notification);
        return new SelfEmailMessage(
            account.Address,
            account.Address,
            account.Address,
            notification.Subject,
            notification.PlainTextBody,
            notification.HtmlBody,
            notification.DeduplicationKey);
    }
}
