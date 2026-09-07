using Aegis.Studio.Web.Operations;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class SameOriginWriteProtectionTests
{
    [Fact]
    public void AuthenticatedApiPostRequiresSameOriginCheck()
    {
        DefaultHttpContext context = CreateAuthenticatedContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/jobs/11111111-1111-1111-1111-111111111111";
        Assert.True(SameOriginWriteProtection.RequiresSameOriginCheck(context));
    }

    [Fact]
    public void AnonymousPublicWriteDoesNotUseCookieOriginPolicy()
    {
        DefaultHttpContext context = new();
        context.Request.Method = "POST";
        context.Request.Path = "/api/bookings";
        Assert.False(SameOriginWriteProtection.RequiresSameOriginCheck(context));
    }

    [Fact]
    public void MatchingOriginIsAccepted()
    {
        DefaultHttpContext context = CreateAuthenticatedContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("studio.example.test");
        context.Request.Headers.Origin = "https://studio.example.test";
        Assert.True(SameOriginWriteProtection.IsSameOrigin(context.Request));
    }

    [Fact]
    public void ForeignOriginIsRejected()
    {
        DefaultHttpContext context = CreateAuthenticatedContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("studio.example.test");
        context.Request.Headers.Origin = "https://evil.example.test";
        Assert.False(SameOriginWriteProtection.IsSameOrigin(context.Request));
    }

    private static DefaultHttpContext CreateAuthenticatedContext()
    {
        DefaultHttpContext context = new();
        ClaimsIdentity identity = new([new Claim(ClaimTypes.Name, "tester")], "test");
        context.User = new ClaimsPrincipal(identity);
        return context;
    }
}
