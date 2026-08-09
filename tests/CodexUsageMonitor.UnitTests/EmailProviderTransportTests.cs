using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodexUsageMonitor.Email.Models;
using CodexUsageMonitor.Email.OAuth;
using CodexUsageMonitor.Email.Security;
using CodexUsageMonitor.Email.Transport;

namespace CodexUsageMonitor.UnitTests;

[TestClass]
public sealed class EmailProviderTransportTests
{
    [TestMethod]
    public async Task GmailApiUsesOnlyGmailSendAndAddressesTheAuthenticatedAccount()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "{\"id\":\"message-id\"}");
        using var client = new HttpClient(handler);
        var sender = new GmailApiSelfNotificationTransport(
            client,
            new FixedTokenProvider(),
            EmailAccountIdentity.Create("person@example.com"));

        var result = await sender.SendSelfNotificationAsync(Notification(), CancellationToken.None);

        Assert.IsTrue(result.Delivered);
        Assert.AreEqual("https://gmail.googleapis.com/gmail/v1/users/me/messages/send", handler.RequestUri?.AbsoluteUri);
        Assert.AreEqual("Bearer", handler.Authorization?.Scheme);
        using var document = JsonDocument.Parse(handler.Body!);
        var raw = document.RootElement.GetProperty("raw").GetString()!;
        var mime = Encoding.UTF8.GetString(Base64UrlDecode(raw));
        StringAssert.Contains(mime, "From: person@example.com");
        StringAssert.Contains(mime, "To: person@example.com");
        Assert.IsFalse(mime.Contains("Cc:", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(mime.Contains("Bcc:", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task MicrosoftGraphUsesDelegatedMeSendMailWithOnlyTheAuthenticatedAccount()
    {
        var handler = new CapturingHandler(HttpStatusCode.Accepted, string.Empty);
        using var client = new HttpClient(handler);
        var sender = new MicrosoftGraphSelfNotificationTransport(
            client,
            new FixedTokenProvider(),
            EmailAccountIdentity.Create("person@example.com"));

        var result = await sender.SendSelfNotificationAsync(Notification(), CancellationToken.None);

        Assert.IsTrue(result.Delivered);
        Assert.AreEqual("https://graph.microsoft.com/v1.0/me/sendMail", handler.RequestUri?.AbsoluteUri);
        using var document = JsonDocument.Parse(handler.Body!);
        var message = document.RootElement.GetProperty("message");
        var recipients = message.GetProperty("toRecipients");
        Assert.AreEqual(1, recipients.GetArrayLength());
        Assert.AreEqual("person@example.com", recipients[0].GetProperty("emailAddress").GetProperty("address").GetString());
        Assert.IsFalse(message.TryGetProperty("ccRecipients", out _));
        Assert.IsFalse(message.TryGetProperty("bccRecipients", out _));
    }

    [TestMethod]
    public void MimeBuilderUsesOneSelfRecipientAndRejectsAddressHeadersFromContent()
    {
        var message = SelfOnlyMessageFactory.Create(EmailAccountIdentity.Create("person@example.com"), Notification());

        var mime = SelfOnlyMimeMessageBuilder.Build(message);

        Assert.AreEqual(1, mime.To.Count);
        Assert.AreEqual("person@example.com", mime.To.Mailboxes.Single().Address);
        Assert.AreEqual(0, mime.Cc.Count);
        Assert.AreEqual(0, mime.Bcc.Count);
        Assert.AreEqual("person@example.com", mime.From.Mailboxes.Single().Address);
    }

    [TestMethod]
    public void ProtonBridgeConnectionRejectsNonLoopbackHostsAndPlaintext()
    {
        Assert.Throws<ArgumentException>(() => SmtpConnectionSettings.ForProtonBridge(
            "smtp.example.com", 1025, SmtpTransportSecurity.StartTls, "bridge-user", "secret"));
        Assert.Throws<ArgumentException>(() => SmtpConnectionSettings.ForProtonBridge(
            "127.0.0.1", 1025, SmtpTransportSecurity.None, "bridge-user", "secret"));
    }

    private static SelfNotification Notification() => new(
        "Codex usage warning",
        "Your allowance is running low.",
        "<p>Your allowance is running low.</p>",
        "usage:warning");

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        return Convert.FromBase64String(padded);
    }

    private sealed class FixedTokenProvider : IAccessTokenProvider
    {
        public Task<OAuthAccessToken> GetAccessTokenAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new OAuthAccessToken("access-token", DateTimeOffset.UtcNow.AddHours(1)));
    }

    private sealed class CapturingHandler(HttpStatusCode statusCode, string responseBody) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
