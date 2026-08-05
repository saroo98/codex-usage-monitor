namespace CodexUsageMonitor.Email.Models;

public sealed record EmailMessage
{
    public EmailMessage(
        string from,
        IReadOnlyList<string> to,
        string subject,
        string plainTextBody,
        string? htmlBody,
        string deduplicationKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(plainTextBody);
        ArgumentException.ThrowIfNullOrWhiteSpace(deduplicationKey);
        if (to.Count is <= 0 or > 16 || to.Any(static value => string.IsNullOrWhiteSpace(value)))
        {
            throw new ArgumentException("Email messages require between one and sixteen recipients.", nameof(to));
        }

        From = from;
        To = to;
        Subject = subject;
        PlainTextBody = plainTextBody;
        HtmlBody = htmlBody;
        DeduplicationKey = deduplicationKey;
    }

    public string From { get; }

    public IReadOnlyList<string> To { get; }

    public string Subject { get; }

    public string PlainTextBody { get; }

    public string? HtmlBody { get; }

    public string DeduplicationKey { get; }
}

public sealed record EmailDeliveryResult(bool Delivered, bool IsTransient, string? SafeErrorCode)
{
    public static EmailDeliveryResult Success { get; } = new(true, false, null);

    public static EmailDeliveryResult Transient(string code) => new(false, true, code);

    public static EmailDeliveryResult Permanent(string code) => new(false, false, code);
}

public sealed record SmtpConnectionSettings(
    string Host,
    int Port,
    bool UseTls,
    string UserName,
    string SecretReference,
    bool UseOAuth2 = false);

public interface IEmailTransport
{
    Task<EmailDeliveryResult> SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
