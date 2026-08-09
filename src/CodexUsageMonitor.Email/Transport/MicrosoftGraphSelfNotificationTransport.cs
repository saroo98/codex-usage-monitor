using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodexUsageMonitor.Email.Models;
using CodexUsageMonitor.Email.OAuth;
using CodexUsageMonitor.Email.Security;

namespace CodexUsageMonitor.Email.Transport;

public sealed class MicrosoftGraphSelfNotificationTransport : ISelfNotificationSender
{
    private static readonly Uri SendEndpoint = new("https://graph.microsoft.com/v1.0/me/sendMail");
    private readonly HttpClient _httpClient;
    private readonly IAccessTokenProvider _accessTokens;
    private readonly EmailAccountIdentity _account;

    public MicrosoftGraphSelfNotificationTransport(HttpClient httpClient, IAccessTokenProvider accessTokens, EmailAccountIdentity account)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _accessTokens = accessTokens ?? throw new ArgumentNullException(nameof(accessTokens));
        _account = account ?? throw new ArgumentNullException(nameof(account));
    }

    public async Task<EmailDeliveryResult> SendSelfNotificationAsync(SelfNotification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var selfMessage = SelfOnlyMessageFactory.Create(_account, notification);
        var token = await _accessTokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var body = new
        {
            message = new
            {
                subject = selfMessage.Subject,
                body = new
                {
                    contentType = notification.HtmlBody is null ? "Text" : "HTML",
                    content = selfMessage.HtmlBody ?? selfMessage.PlainTextBody,
                },
                toRecipients = new[]
                {
                    new { emailAddress = new { address = selfMessage.ToAddress } },
                },
                internetMessageHeaders = new[]
                {
                    new { name = "X-Codex-Usage-Monitor-Event", value = selfMessage.DeduplicationKey },
                },
            },
            saveToSentItems = false,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, SendEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        return Classify(response.StatusCode);
    }

    private static EmailDeliveryResult Classify(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        if (code is >= 200 and < 300)
        {
            return EmailDeliveryResult.Success;
        }

        return code is 408 or 429 or >= 500
            ? EmailDeliveryResult.Transient("email.graph_temporary_failure")
            : EmailDeliveryResult.Permanent(code is 401 or 403 ? "email.authorization_rejected" : "email.graph_rejected");
    }
}
