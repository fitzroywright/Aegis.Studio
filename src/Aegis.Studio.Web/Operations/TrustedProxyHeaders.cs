using System.Net;

namespace Aegis.Studio.Web.Operations;

public static class TrustedProxyHeaders
{
    public static void UseStudioTrustedProxyHeaders(this WebApplication app)
    {
        IConfiguration configuration = app.Services.GetRequiredService<IConfiguration>();
        IWebHostEnvironment environment = app.Services.GetRequiredService<IWebHostEnvironment>();

        app.Use(async (context, next) =>
        {
            if (!environment.IsDevelopment() && configuration.GetValue("Security:TrustForwardedProto", false))
            {
                string forwardedProto = FirstHeaderValue(context.Request.Headers["X-Forwarded-Proto"].ToString());
                if (forwardedProto.Equals("https", StringComparison.OrdinalIgnoreCase) || forwardedProto.Equals("http", StringComparison.OrdinalIgnoreCase))
                {
                    context.Request.Scheme = forwardedProto.ToLowerInvariant();
                }

                string forwardedHost = FirstHeaderValue(context.Request.Headers["X-Forwarded-Host"].ToString());
                if (!string.IsNullOrWhiteSpace(forwardedHost) && HostString.FromUriComponent(forwardedHost).HasValue)
                {
                    context.Request.Host = HostString.FromUriComponent(forwardedHost);
                }

                string forwardedFor = FirstHeaderValue(context.Request.Headers["X-Forwarded-For"].ToString());
                if (IPAddress.TryParse(forwardedFor, out IPAddress? address))
                {
                    context.Connection.RemoteIpAddress = address;
                }
            }

            await next();
        });
    }

    private static string FirstHeaderValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        int separator = value.IndexOf(',');
        return (separator >= 0 ? value[..separator] : value).Trim();
    }
}
