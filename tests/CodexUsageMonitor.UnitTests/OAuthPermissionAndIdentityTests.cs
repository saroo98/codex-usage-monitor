using System.Net;
using System.Text;
using CodexUsageMonitor.Email.OAuth;

namespace CodexUsageMonitor.UnitTests;

[TestClass]
public sealed class OAuthPermissionAndIdentityTests
{
    [TestMethod]
    public void GoogleRequestsOnlySendAndMinimumIdentityScopes()
    {
        CollectionAssert.AreEquivalent(
            new[] { "openid", "email", "https://www.googleapis.com/auth/gmail.send" },
            GooglePkceAuthorizationFlow.GmailApiScopes.ToArray());
        Assert.IsFalse(GooglePkceAuthorizationFlow.GmailApiScopes.Any(scope =>
            scope.Contains("mail.google.com", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void MicrosoftRequestsDelegatedSendAndSignedInProfileOnly()
    {
        CollectionAssert.AreEquivalent(
            new[] { "offline_access", "openid", "email", "User.Read", "Mail.Send" },
            MicrosoftPkceAuthorizationFlow.GraphScopes.ToArray());
        Assert.IsFalse(MicrosoftPkceAuthorizationFlow.GraphScopes.Any(scope =>
            scope.Contains("Mail.Read", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task GoogleIdentityComesFromTheAuthenticatedProviderResponse()
    {
        using var client = new HttpClient(new JsonHandler("{\"email\":\"person@example.com\",\"email_verified\":true}"));
        var resolver = new ProviderEmailAccountIdentityResolver(client);

        var identity = await resolver.ResolveGoogleAsync(
            new OAuthAccessToken("token", DateTimeOffset.UtcNow.AddHours(1)),
            CancellationToken.None);

        Assert.AreEqual("person@example.com", identity.Address);
    }

    [TestMethod]
    public async Task MicrosoftIdentityUsesMailThenUserPrincipalName()
    {
        using var client = new HttpClient(new JsonHandler("{\"mail\":null,\"userPrincipalName\":\"person@example.com\"}"));
        var resolver = new ProviderEmailAccountIdentityResolver(client);

        var identity = await resolver.ResolveMicrosoftAsync(
            new OAuthAccessToken("token", DateTimeOffset.UtcNow.AddHours(1)),
            CancellationToken.None);

        Assert.AreEqual("person@example.com", identity.Address);
    }

    private sealed class JsonHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
