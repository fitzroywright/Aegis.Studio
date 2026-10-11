using System.Security.Claims;

namespace Aegis.Studio.Web.WorkDay;

public sealed class WorkDayDeviceTokenMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context,WorkDayStore store)
    {
        if(context.Request.Headers.TryGetValue("X-Aegis-WorkDay-Token",out var supplied) &&
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
            else{context.Response.StatusCode=StatusCodes.Status401Unauthorized;return;}
        }
        if(context.User.Identity?.AuthenticationType=="WorkDayDeviceToken")
        {
            string path=context.Request.Path.Value??"";
            string[] reportingPaths=["/api/workday/bootstrap","/api/workday/availability","/api/workday/operations","/api/workday/device-session/revoke","/api/workday/client-observation"];
            if(!reportingPaths.Contains(path,StringComparer.OrdinalIgnoreCase))
            {context.Response.StatusCode=StatusCodes.Status403Forbidden;return;}
        }
        await next(context);
    }
}
