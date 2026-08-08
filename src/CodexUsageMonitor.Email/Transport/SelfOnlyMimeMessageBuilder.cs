using CodexUsageMonitor.Email.Models;
using MimeKit;

namespace CodexUsageMonitor.Email.Transport;

public static class SelfOnlyMimeMessageBuilder
{
    private const int MaximumSubjectCharacters = 160;
    private const int MaximumEventCharacters = 200;

    public static MimeMessage Build(SelfEmailMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!string.Equals(message.AccountAddress, message.FromAddress, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(message.AccountAddress, message.ToAddress, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The email address boundary is not self-only.");
        }

        var mimeMessage = new MimeMessage();
        mimeMessage.From.Add(MailboxAddress.Parse(message.AccountAddress));
        mimeMessage.To.Add(MailboxAddress.Parse(message.AccountAddress));
        mimeMessage.Subject = message.Subject[..Math.Min(message.Subject.Length, MaximumSubjectCharacters)];
        mimeMessage.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId("codex-usage-monitor.local");
        mimeMessage.Headers.Add(
            "X-Codex-Usage-Monitor-Event",
            message.DeduplicationKey[..Math.Min(message.DeduplicationKey.Length, MaximumEventCharacters)]);
        mimeMessage.Body = new BodyBuilder
        {
            TextBody = message.PlainTextBody,
            HtmlBody = message.HtmlBody,
        }.ToMessageBody();
        return mimeMessage;
    }
}
