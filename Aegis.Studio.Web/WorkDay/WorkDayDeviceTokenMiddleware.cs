using System.Security.Claims;

namespace Aegis.Studio.Web.WorkDay;

public sealed class WorkDayDeviceTokenMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context,WorkDayStore store)
    {
        if(context.User.Identity?.IsAuthenticated!=true &&
           context.Request.Headers.TryGetValue("X-Aegis-WorkDay-Token",out var supplied) &&
           !string.IsNullOrWhiteSpace(supplied))
        {
            var validated=store.ValidateDeviceToken(supplied.ToString());
            if(validated is { } identity)
            {
                ClaimsIdentity claims=new(
                [
                    new Claim(ClaimTypes.NameIdentifier,identity.UserName),
                    new Claim("login_name",identity.UserName),
                    new Claim("workday_device_id",identity.DeviceId),
                    new Claim("authentication_source","WorkDayDeviceToken")
                ],
                "WorkDayDeviceToken");
                context.User=new ClaimsPrincipal(claims);
            }
        }
        await next(context);
    }
}
