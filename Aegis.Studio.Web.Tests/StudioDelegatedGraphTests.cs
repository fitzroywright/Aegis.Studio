using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aegis.Studio.Web.Email;
using Common.Messaging;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class StudioDelegatedGraphTests
{
    [Fact]
    public void CacheIsAuthenticatedAndRejectsTamperingOrWrongKey()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32), plain = Encoding.UTF8.GetBytes("private-refresh-token");
        byte[] encrypted = StudioDelegatedGraphSession.Encrypt(plain, key);
        Assert.Equal(plain, StudioDelegatedGraphSession.Decrypt(encrypted, key));
        Assert.DoesNotContain("private-refresh-token", Encoding.UTF8.GetString(encrypted));
        Assert.Throws<CryptographicException>(() => StudioDelegatedGraphSession.Decrypt(encrypted, RandomNumberGenerator.GetBytes(32)));
        encrypted[^1] ^= 1;
        Assert.Throws<CryptographicException>(() => StudioDelegatedGraphSession.Decrypt(encrypted, key));
    }

    [Fact]
    public async Task AuthorizationUsesTenantPkceAndConfiguredCallback()
    {
        using var f = new Fixture();
        string url = await f.Session.AuthorizationUrlAsync("state-value", "verifier-value", default);
        Assert.Contains("/11111111-1111-1111-1111-111111111111/oauth2/v2.0/authorize?", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.DoesNotContain("verifier-value", url);
        Assert.Contains("Mail.Send.Shared", Uri.UnescapeDataString(url));
        Assert.Contains("offline_access", url);
    }

    [Fact]
    public async Task WrongHubIsRejectedBeforePersistence()
    {
        using var f = new Fixture();
        f.Handler.Mailbox = "other@studio.test";
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Session.CompleteAsync("code", "verifier", default));
        Assert.False(File.Exists(Path.Combine(f.Directory, "session.enc")));
    }

    [Fact]
    public async Task CacheSurvivesRestartAndRotatesRefreshTokensUnderConcurrentUse()
    {
        using var f = new Fixture();
        f.Handler.InitialExpires = 0;
        await f.Session.CompleteAsync("code", "verifier", default);
        string cache = await File.ReadAllTextAsync(Path.Combine(f.Directory, "session.enc"));
        Assert.DoesNotContain("refresh-one", cache);
        var restarted = f.NewSession();
        var tokens = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => restarted.AccessTokenAsync(default)));
        Assert.All(tokens, t => Assert.Equal("renewed-token", t));
        Assert.Equal(1, f.Handler.Refreshes);
        Assert.Equal("refresh-one", f.Handler.LastRefresh);
        Assert.Equal("renewed-token", await f.NewSession().AccessTokenAsync(default));
        await restarted.DisconnectAsync(default);
        await Assert.ThrowsAsync<StudioMailReconnectRequiredException>(() => restarted.AccessTokenAsync(default));
    }

    [Fact]
    public async Task RevokedAuthorizationDoesNotSubmitMailOrFallBackToAppCredentials()
    {
        using var f = new Fixture();
        f.Handler.InitialExpires = 0;
        await f.Session.CompleteAsync("code", "verifier", default);
        f.Handler.Revoked = true;
        await Assert.ThrowsAsync<StudioMailReconnectRequiredException>(() => f.Channel.SendAsync(Recipient, Request(), Guid.NewGuid()));
        Assert.Equal(0, f.Handler.Sends);
    }

    [Fact]
    public async Task DisconnectInvalidatesPreviouslyStartedAuthorization()
    {
        using var f = new Fixture();
        long generation = f.Session.Generation;
        await f.Session.DisconnectAsync(default);
        await Assert.ThrowsAsync<StudioMailReconnectRequiredException>(() => f.Session.CompleteAsync("code", "verifier", default, generation));
        Assert.False(File.Exists(Path.Combine(f.Directory, "session.enc")));
    }

    [Fact]
    public async Task SentItemsVerificationUsesDelegatedHubAndActualInternetId()
    {
        using var f = new Fixture();
        await f.Session.CompleteAsync("code", "verifier", default);
        var id = Guid.NewGuid();
        var request = Request();
        await f.Channel.SendAsync(Recipient, request, id);
        var delivery = new OutboundEmail(id, Guid.NewGuid(), "weddings@studio.test", "client@outside.test", "Test", "Body", null, "Sending", DateTimeOffset.UtcNow, null);
        var verified = await new StudioGraphSentMessageVerifier(new HttpClient(f.Handler, false), f.Configuration, f.SecretResolver, f.Session)
            .VerifyAsync(delivery, id.ToString("D"), "weddings@studio.test", DateTimeOffset.UtcNow.AddMinutes(-1), default);
        Assert.Equal("<actual@exchange.test>", verified.InternetMessageId);
        Assert.Equal("provider-id", verified.ProviderId);
        Assert.Equal(1, f.Handler.Sends);
    }

    [Fact]
    public async Task DelegatedSendPreservesMimeIdentityAttachmentsAndThreading()
    {
        using var f = new Fixture();
        await f.Session.CompleteAsync("code", "verifier", default);
        await f.Channel.SendAsync(Recipient, Request(), Guid.NewGuid());
        Assert.Equal("/v1.0/me/sendMail", f.Handler.SendPath);
        Assert.Equal("initial-token", f.Handler.SendToken);
        Assert.Contains("From: weddings@studio.test", f.Handler.Mime);
        Assert.Contains("Reply-To: weddings@studio.test", f.Handler.Mime);
        Assert.Contains("In-Reply-To: <original@outside.test>", f.Handler.Mime);
        Assert.Contains("Message-ID: <stable@studio.test>", f.Handler.Mime);
        Assert.Contains("AQID", f.Handler.Mime);
        Assert.Equal(1, f.Handler.Sends);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(500)]
    public async Task DefinitiveRejectionAndUncertainOutcomeAreDistinct(int status)
    {
        using var f = new Fixture();
        await f.Session.CompleteAsync("code", "verifier", default);
        f.Handler.SendStatus = (HttpStatusCode)status;
        if (status == 403)
        {
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => f.Channel.SendAsync(Recipient, Request(), Guid.NewGuid()));
            Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        }
        else await Assert.ThrowsAsync<ExternalDeliveryOutcomeUnknownException>(() => f.Channel.SendAsync(Recipient, Request(), Guid.NewGuid()));
        Assert.Equal(1, f.Handler.Sends);
    }

    private static readonly MessageRecipient Recipient = new("client", "Client", "client@outside.test");
    private static MessageRequest Request() => new() { Title = "Test", Body = "Body", BodyIsHtml = false,
        Attachments = [new MessageAttachment("test.bin", "application/octet-stream", [1,2,3])], Metadata = new Dictionary<string,string>
        { ["Email.MessageId"] = "<stable@studio.test>", ["Email.From"] = "weddings@studio.test", ["Email.ReplyTo"] = "weddings@studio.test", ["Email.InReplyTo"] = "<original@outside.test>" } };

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "studio-oauth-test-" + Guid.NewGuid().ToString("N"));
        public Handler Handler { get; } = new();
        private readonly Secrets secrets = new();
        private readonly IConfiguration configuration;
        public IConfiguration Configuration => configuration;
        public IChannelSecretResolver SecretResolver => secrets;
        public StudioDelegatedGraphSession Session { get; }
        public StudioDelegatedGraphChannel Channel { get; }
        public Fixture()
        {
            configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            {
                ["Messaging:MicrosoftGraph:TenantId"] = "11111111-1111-1111-1111-111111111111",
                ["Messaging:MicrosoftGraph:ClientId"] = "client",
                ["Messaging:MicrosoftGraph:Email:SenderUpn"] = "hub@studio.test",
                [StudioDelegatedGraphSession.Prefix + "Enabled"] = "true",
                [StudioDelegatedGraphSession.Prefix + "RedirectUri"] = "https://studio.test/oauth/outbound-mail/callback",
                [StudioDelegatedGraphSession.Prefix + "CacheDirectory"] = Directory
            }).Build();
            Session = NewSession();
            Channel = new(new HttpClient(Handler, false), Session, configuration);
        }
        public StudioDelegatedGraphSession NewSession() => new(configuration, secrets, new Factory(Handler));
        public void Dispose() { Handler.Dispose(); if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
    }
    private sealed class Secrets : IChannelSecretResolver
    {
        private readonly string key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        public Task<string?> GetSecretAsync(string name, CancellationToken token = default) => Task.FromResult<string?>(name.EndsWith("outbound-cache-key") ? key : "client-secret");
    }
    private sealed class Factory(Handler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, false); }
    private sealed class Handler : HttpMessageHandler
    {
        public string Mailbox { get; set; } = "hub@studio.test";
        public int InitialExpires { get; set; } = 3600;
        public bool Revoked { get; set; }
        public int Refreshes { get; private set; }
        public string? LastRefresh { get; private set; }
        public int Sends { get; private set; }
        public string? SendPath { get; private set; }
        public string? SendToken { get; private set; }
        public string Mime { get; private set; } = "";
        public HttpStatusCode SendStatus { get; set; } = HttpStatusCode.Accepted;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
            {
                string body = await request.Content!.ReadAsStringAsync(token);
                Assert.DoesNotContain("client_credentials", body);
                bool refresh = body.Contains("grant_type=refresh_token");
                if (refresh)
                {
                    Refreshes++;
                    LastRefresh = Uri.UnescapeDataString(body.Split('&').Single(p => p.StartsWith("refresh_token=")).Split('=')[1]);
                    if (Revoked) return new(HttpStatusCode.BadRequest) { Content = new StringContent("private-provider-error") };
                }
                else Assert.Contains("code_verifier=verifier", body);
                return Json(new { access_token = refresh ? "renewed-token" : "initial-token", refresh_token = refresh ? "refresh-two" : "refresh-one",
                    expires_in = refresh ? 3600 : InitialExpires, scope = "User.Read Mail.Send.Shared Mail.Read" });
            }
            Assert.Equal("graph.microsoft.com", request.RequestUri.Host);
            if (request.RequestUri.AbsolutePath == "/v1.0/me/mailFolders/sentitems/messages")
            {
                Assert.Equal("initial-token", request.Headers.Authorization?.Parameter);
                string correlation = System.Text.RegularExpressions.Regex.Match(Mime, @"X-Studio-Message-Id: ([^\r\n]+)").Groups[1].Value;
                return Json(new { value = new[] { new { id = "provider-id", internetMessageId = "<actual@exchange.test>",
                    internetMessageHeaders = new[] { new { name = "X-Studio-Message-Id", value = correlation } },
                    from = new { emailAddress = new { address = "weddings@studio.test" } },
                    toRecipients = new[] { new { emailAddress = new { address = "client@outside.test" } } },
                    replyTo = new[] { new { emailAddress = new { address = "weddings@studio.test" } } } } } });
            }
            if (request.Method == HttpMethod.Post)
            {
                Sends++; SendPath = request.RequestUri.AbsolutePath; SendToken = request.Headers.Authorization?.Parameter;
                Mime = Encoding.UTF8.GetString(Convert.FromBase64String(await request.Content!.ReadAsStringAsync(token)));
                return new(SendStatus);
            }
            Assert.Equal("/v1.0/me", request.RequestUri.AbsolutePath);
            return Json(new { id = "hub-id", mail = Mailbox, userPrincipalName = Mailbox });
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    }
}
