using Aegis.Studio.Web.Operations;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class ProductionHardeningTests
{
    [Theory]
    [InlineData("POST", "/api/auth/login", "auth-login")]
    [InlineData("POST", "/api/bookings", "public-booking")]
    [InlineData("POST", "/api/bookings/11111111-1111-1111-1111-111111111111/payment-confirmations", "payment-confirmation")]
    [InlineData("GET", "/public/galleries/token/items/11111111-1111-1111-1111-111111111111/preview", "public-gallery")]
    [InlineData("GET", "/g/token", "public-gallery")]
    public void PublicSensitiveRoutesHaveRateLimits(string method, string path, string expectedScope)
    {
        DefaultHttpContext context = new();
        context.Request.Method = method;
        context.Request.Path = path;

        RateLimitPolicy? policy = ProductionRequestPolicy.GetRateLimitPolicy(context.Request);

        Assert.NotNull(policy);
        Assert.Equal(expectedScope, policy!.Scope);
        Assert.True(policy.PermitLimit > 0);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void MutatingRequestsAreAudited(string method)
    {
        DefaultHttpContext context = new();
        context.Request.Method = method;
        Assert.True(ProductionRequestPolicy.ShouldAudit(context.Request));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public void ReadOnlyRequestsAreNotMutationAudits(string method)
    {
        DefaultHttpContext context = new();
        context.Request.Method = method;
        Assert.False(ProductionRequestPolicy.ShouldAudit(context.Request));
    }

    [Fact]
    public void PublicOriginalGalleryDownloadIsIdentifiedSeparately()
    {
        DefaultHttpContext context = new();
        context.Request.Method = "GET";
        context.Request.Path = "/public/galleries/token/items/11111111-1111-1111-1111-111111111111/original";
        Assert.True(ProductionRequestPolicy.IsPublicGalleryOriginal(context.Request));

        context.Request.Path = "/public/galleries/token/items/11111111-1111-1111-1111-111111111111/preview";
        Assert.False(ProductionRequestPolicy.IsPublicGalleryOriginal(context.Request));
    }
}
