using System.Security.Claims;
using System.Text;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.PublicApi;
using Common.Secrets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;
namespace Aegis.Studio.Web.Tests;

public sealed class PublicV1SecurityTests
{
    [Fact]
    public async Task AdministratorCookieCannotSubstituteForAValidIntegrationSignature()
    {
        var configuration=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"BookingApi:Clients:website:V1Scopes:0","bookings.write"}}).Build();
        using var source=NpgsqlDataSource.Create("Host=localhost;Database=unused;Username=unused");
        var context=new DefaultHttpContext();context.Request.Method="POST";context.Request.Path="/api/public/v1/bookings";
        context.User=new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name,"administrator"),new Claim(ClaimTypes.Role,"Administrator")],"cookie"));
        context.Request.Headers["X-Studio-Client"]="website";context.Request.Headers["X-Studio-Timestamp"]=DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        context.Request.Headers["X-Studio-Nonce"]="unique-nonce";context.Request.Headers["X-Studio-Signature"]=new string('0',64);
        var security=new PublicV1Security(configuration,new TestSecrets(),new StudioDatabase(source));
        var error=await Assert.ThrowsAsync<PublicApiException>(()=>security.Integration(context,"bookings.write",Encoding.UTF8.GetBytes("{}")));
        Assert.Equal(401,error.Status);
        error=await Assert.ThrowsAsync<PublicApiException>(()=>security.Integration(context,"bookings.read",[]));
        Assert.Equal(403,error.Status);
    }
    [Fact]
    public void WebhookRetriesUseFreshTimestampButStableEventIdentity()
    {
        const string payload="{\"schemaVersion\":1}";
        string first=PublicWebhookSigning.Sign("secret","1000","event",payload);
        Assert.NotEqual(first,PublicWebhookSigning.Sign("secret","1001","event",payload));
        Assert.NotEqual(first,PublicWebhookSigning.Sign("secret","1000","different",payload));
        Assert.Equal(TimeSpan.FromSeconds(15),PublicWebhookSigning.RetryDelay(1));
        Assert.Equal(TimeSpan.FromHours(1),PublicWebhookSigning.RetryDelay(100));
    }
    private sealed class TestSecrets:ISecretProvider
    {
        public Task<string?> GetAsync(string name,CancellationToken cancellationToken=default)=>Task.FromResult<string?>("test-secret");
        public Task<string> GetRequiredAsync(string name,CancellationToken cancellationToken=default)=>Task.FromResult("test-secret");
    }
}
