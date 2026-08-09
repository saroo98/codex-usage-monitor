using System.Net.Mail;

namespace CodexUsageMonitor.Email.Models;

public sealed record EmailAccountIdentity
{
    private EmailAccountIdentity(string address) => Address = address;

    public string Address { get; }

    public static EmailAccountIdentity Create(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        RejectHeaderCharacters(address, nameof(address));
        var trimmed = address.Trim();
        var parsed = new MailAddress(trimmed);
        if (!string.Equals(parsed.Address, trimmed, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The account email address is invalid.", nameof(address));
        }

        return new EmailAccountIdentity(parsed.Address);
    }

    internal static void RejectHeaderCharacters(string value, string parameterName)
    {
        if (value.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("Email header values cannot contain control characters.", parameterName);
        }
    }
}

public sealed record SelfNotification
{
    public SelfNotification(string subject, string plainTextBody, string? htmlBody, string deduplicationKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(plainTextBody);
        ArgumentException.ThrowIfNullOrWhiteSpace(deduplicationKey);
        EmailAccountIdentity.RejectHeaderCharacters(subject, nameof(subject));
        EmailAccountIdentity.RejectHeaderCharacters(deduplicationKey, nameof(deduplicationKey));
        Subject = subject.Trim();
        PlainTextBody = plainTextBody;
        HtmlBody = htmlBody;
        DeduplicationKey = deduplicationKey;
    }

    public string Subject { get; }
    public string PlainTextBody { get; }
    public string? HtmlBody { get; }
    public string DeduplicationKey { get; }
}

public sealed record SelfEmailMessage(
    string AccountAddress,
    string FromAddress,
    string ToAddress,
    string Subject,
    string PlainTextBody,
    string? HtmlBody,
    string DeduplicationKey);

public interface ISelfNotificationSender
{
    Task<EmailDeliveryResult> SendSelfNotificationAsync(SelfNotification notification, CancellationToken cancellationToken);
}

public sealed record EmailDeliveryResult(bool Delivered, bool IsTransient, string? SafeErrorCode)
{
    public static EmailDeliveryResult Success { get; } = new(true, false, null);

    public static EmailDeliveryResult Transient(string code) => new(false, true, code);

    public static EmailDeliveryResult Permanent(string code) => new(false, false, code);
}

public enum SmtpTransportSecurity
{
    None,
    StartTls,
    Tls,
}

public sealed record SmtpConnectionSettings(
    string Host,
    int Port,
    bool UseTls,
    string UserName,
    string SecretReference,
    bool UseOAuth2 = false)
{
    public SmtpTransportSecurity Security { get; init; } = UseTls ? SmtpTransportSecurity.StartTls : SmtpTransportSecurity.None;

    public bool RequireLoopback { get; init; }

    public static SmtpConnectionSettings ForProtonBridge(
        string host,
        int port,
        SmtpTransportSecurity security,
        string userName,
        string secretReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (host is not ("127.0.0.1" or "::1" or "localhost"))
        {
            throw new ArgumentException("Proton Mail Bridge must use the local loopback service.", nameof(host));
        }

        if (security is SmtpTransportSecurity.None)
        {
            throw new ArgumentException("Proton Mail Bridge requires encrypted transport.", nameof(security));
        }

        return new SmtpConnectionSettings(host, port, true, userName, secretReference)
        {
            Security = security,
            RequireLoopback = true,
        };
    }
}
