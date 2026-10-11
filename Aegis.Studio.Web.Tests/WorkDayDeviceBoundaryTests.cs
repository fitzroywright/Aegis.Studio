using System.Security.Claims;
using Aegis.Studio.Web.WorkDay;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class WorkDayDeviceBoundaryTests
{
    [Theory]
    [InlineData("/api/workday/bootstrap",true)]
    [InlineData("/api/workday/operations",true)]
    [InlineData("/api/workday/availability",true)]
    [InlineData("/api/workday/device-session",false)]
    [InlineData("/api/workday/assignments/11111111-1111-1111-1111-111111111111/uploads",false)]
    [InlineData("/api/workday/time-summary",false)]
    [InlineData("/api/jobs",false)]
    [InlineData("/api/production/orders",false)]
    public async Task PhotographerDeviceCannotBecomeAGeneralStudioSession(string path,bool allowed)
    {
        bool reached=false;
        var middleware=new WorkDayDeviceTokenMiddleware(_=>{reached=true;return Task.CompletedTask;});
        var context=new DefaultHttpContext{User=new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,"photographer")],"WorkDayDeviceToken"))};
        context.Request.Path=path;
        await middleware.InvokeAsync(context,new WorkDayStore(null!));
        Assert.Equal(allowed,reached);
        if(!allowed)Assert.Equal(StatusCodes.Status403Forbidden,context.Response.StatusCode);
    }
}
