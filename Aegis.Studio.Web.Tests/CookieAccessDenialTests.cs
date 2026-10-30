using Aegis.Studio.Web.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class CookieAccessDenialTests
{
    [Theory]
    [InlineData("/api/security/global-roles/")]
    [InlineData("/api/admin/security/accounts/")]
    public async Task DeniedApiDoesNotTurnIntoSuccessfulLoginHtml(string path)
    {
        var context = Context(path);
        await StudioSignIn.RedirectAccessDeniedAsync(context);
        Assert.Equal(403, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Location"));
    }

    [Fact]
    public async Task DeniedDocumentRetainsNormalLoginNavigation()
    {
        var context = Context("/staff");
        await StudioSignIn.RedirectAccessDeniedAsync(context);
        Assert.Equal(302, context.Response.StatusCode);
        Assert.Equal("https://studio.example.test/login", context.Response.Headers.Location.ToString());
    }

    private static RedirectContext<CookieAuthenticationOptions> Context(string path)
    {
        var http = new DefaultHttpContext(); http.Request.Path = path;
        return new(http, new AuthenticationScheme("Cookies", null, typeof(CookieAuthenticationHandler)),
            new CookieAuthenticationOptions(), new AuthenticationProperties(), "https://studio.example.test/login");
    }
}
