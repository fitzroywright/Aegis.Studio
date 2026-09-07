namespace Aegis.Studio.Web.Operations;

public static class SameOriginWriteProtection
{
    public static void UseStudioSameOriginWriteProtection(this WebApplication app)
    {
        IWebHostEnvironment environment = app.Services.GetRequiredService<IWebHostEnvironment>();
        IConfiguration configuration = app.Services.GetRequiredService<IConfiguration>();

        app.Use(async (context, next) =>
        {
            if (!environment.IsDevelopment()
                && configuration.GetValue("Security:RequireSameOriginWrites", true)
                && RequiresSameOriginCheck(context)
                && !IsSameOrigin(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "Cross-site write request rejected.", correlationId = context.TraceIdentifier });
                return;
            }

            await next();
        });
    }

    public static bool RequiresSameOriginCheck(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true) return false;
        if (!context.Request.Path.StartsWithSegments("/api")) return false;
        string method = context.Request.Method;
        return !method.Equals("GET", StringComparison.OrdinalIgnoreCase)
            && !method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
            && !method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase)
            && !method.Equals("TRACE", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSameOrigin(HttpRequest request)
    {
        string originText = request.Headers.Origin.ToString();
        if (!string.IsNullOrWhiteSpace(originText)
            && Uri.TryCreate(originText, UriKind.Absolute, out Uri? origin))
        {
            string expectedScheme = request.IsHttps ? "https" : "http";
            return origin.Scheme.Equals(expectedScheme, StringComparison.OrdinalIgnoreCase)
                && origin.Authority.Equals(request.Host.Value, StringComparison.OrdinalIgnoreCase);
        }

        string fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
        return fetchSite.Equals("same-origin", StringComparison.OrdinalIgnoreCase);
    }
}
