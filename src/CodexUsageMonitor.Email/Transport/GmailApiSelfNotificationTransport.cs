using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodexUsageMonitor.Email.Models;
using CodexUsageMonitor.Email.OAuth;
using CodexUsageMonitor.Email.Security;

namespace CodexUsageMonitor.Email.Transport;

public sealed class GmailApiSelfNotificationTransport : ISelfNotificationSender
{
    private static readonly Uri SendEndpoint = new("https://gmail.googleapis.com/gmail/v1/users/me/messages/send");
    private readonly HttpClient _httpClient;
    private readonly IAccessTokenProvider _accessTokens;
    private readonly EmailAccountIdentity _account;

    public GmailApiSelfNotificationTransport(HttpClient httpClient, IAccessTokenProvider accessTokens, EmailAccountIdentity account)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _accessTokens = accessTokens ?? throw new ArgumentNullException(nameof(accessTokens));
        _account = account ?? throw new ArgumentNullException(nameof(account));
    }

    public async Task<EmailDeliveryResult> SendSelfNotificationAsync(SelfNotification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var message = SelfOnlyMessageFactory.Create(_account, notification);
        using var mime = SelfOnlyMimeMessageBuilder.Build(message);
        await using var buffer = new MemoryStream();
        await mime.WriteToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var raw = Convert.ToBase64String(buffer.GetBuffer(), 0, checked((int)buffer.Length))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var token = await _accessTokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, SendEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { raw }), Encoding.UTF8, "application/json"),
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
            ? EmailDeliveryResult.Transient("email.gmail_api_temporary_failure")
            : EmailDeliveryResult.Permanent(code is 401 or 403 ? "email.authorization_rejected" : "email.gmail_api_rejected");
    }
}
