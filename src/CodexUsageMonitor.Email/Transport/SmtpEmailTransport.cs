using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using CodexUsageMonitor.Core.Security;
using CodexUsageMonitor.Email.Models;
using CodexUsageMonitor.Email.OAuth;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace CodexUsageMonitor.Email.Transport;

public sealed class SmtpEmailTransport : IEmailTransport
{
    private const int MaximumMessageCharacters = 256 * 1024;
    private readonly SmtpConnectionSettings _settings;
    private readonly ISecretStore _secrets;
    private readonly IAccessTokenProvider? _accessTokens;
    private readonly ILogger<SmtpEmailTransport> _logger;

    public SmtpEmailTransport(
        SmtpConnectionSettings settings,
        ISecretStore secrets,
        IAccessTokenProvider? accessTokens,
        ILogger<SmtpEmailTransport> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _accessTokens = accessTokens;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (_settings.UseOAuth2 && _accessTokens is null)
        {
            throw new ArgumentException("OAuth SMTP requires an access-token provider.", nameof(accessTokens));
        }
    }

    public async Task<EmailDeliveryResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.PlainTextBody.Length + (message.HtmlBody?.Length ?? 0) > MaximumMessageCharacters)
        {
            return EmailDeliveryResult.Permanent("email.message_too_large");
        }

        try
        {
            var mimeMessage = BuildMessage(message);
            using var client = new SmtpClient
            {
                Timeout = 30_000,
            };
            await client.ConnectAsync(
                _settings.Host,
                _settings.Port,
                ResolveSocketOptions(_settings),
                cancellationToken).ConfigureAwait(false);
            if (_settings.UseOAuth2)
            {
                var token = await _accessTokens!.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
                await client.AuthenticateAsync(
                    new SaslMechanismOAuth2(_settings.UserName, token.Value),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var passwordBytes = await _secrets.GetAsync(_settings.SecretReference, cancellationToken).ConfigureAwait(false);
                if (passwordBytes is null || passwordBytes.Length == 0)
                {
                    return EmailDeliveryResult.Permanent("email.credential_missing");
                }

                try
                {
                    var password = Encoding.UTF8.GetString(passwordBytes);
                    await client.AuthenticateAsync(_settings.UserName, password, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(passwordBytes);
                }
            }

            await client.SendAsync(mimeMessage, cancellationToken).ConfigureAwait(false);
            await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
            return EmailDeliveryResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (System.Security.Authentication.AuthenticationException exception)
        {
            _logger.LogWarning(exception, "SMTP TLS negotiation failed.");
            return EmailDeliveryResult.Permanent("email.tls_failed");
        }
        catch (ServiceNotAuthenticatedException exception)
        {
            _logger.LogWarning(exception, "SMTP authentication was rejected.");
            return EmailDeliveryResult.Permanent("email.authentication_rejected");
        }
        catch (SmtpCommandException exception)
        {
            _logger.LogWarning(exception, "SMTP command failed with status {StatusCode}.", exception.StatusCode);
            return IsTransient(exception.StatusCode)
                ? EmailDeliveryResult.Transient("email.smtp_temporary_failure")
                : EmailDeliveryResult.Permanent("email.smtp_rejected");
        }
        catch (Exception exception) when (exception is SmtpProtocolException or ServiceNotConnectedException or IOException or SocketException or TimeoutException)
        {
            _logger.LogWarning(exception, "SMTP delivery failed transiently.");
            return EmailDeliveryResult.Transient("email.transport_unavailable");
        }
        catch (FormatException exception)
        {
            _logger.LogWarning(exception, "Email addressing is invalid.");
            return EmailDeliveryResult.Permanent("email.invalid_address");
        }
    }

    private static MimeMessage BuildMessage(EmailMessage message)
    {
        var mimeMessage = new MimeMessage();
        mimeMessage.From.Add(MailboxAddress.Parse(message.From));
        foreach (var recipient in message.To)
        {
            mimeMessage.To.Add(MailboxAddress.Parse(recipient));
        }
        mimeMessage.Subject = message.Subject.Trim()[..Math.Min(message.Subject.Trim().Length, 160)];
        mimeMessage.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId("codex-usage-monitor.local");
        mimeMessage.Headers.Add("X-Codex-Usage-Monitor-Event", message.DeduplicationKey[..Math.Min(message.DeduplicationKey.Length, 200)]);
        mimeMessage.Body = new BodyBuilder
        {
            TextBody = message.PlainTextBody,
            HtmlBody = message.HtmlBody,
        }.ToMessageBody();
        return mimeMessage;
    }

    private static SecureSocketOptions ResolveSocketOptions(SmtpConnectionSettings settings)
    {
        if (!settings.UseTls)
        {
            return SecureSocketOptions.None;
        }

        return settings.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
    }

    private static bool IsTransient(SmtpStatusCode statusCode)
    {
        var numeric = (int)statusCode;
        return numeric is >= 400 and < 500;
    }
}
