using Aegis.Pos.Core;
using Aegis.Studio.Web.Pos;

namespace Aegis.Studio.Web.Operations;

public static class SensitiveRouteAuthorization
{
    private static readonly string[] administratorOnlyPaths =
    [
        "/api/email/status",
        "/api/email/unrouted",
        "/api/email/outbox",
        "/api/booking-security/status",
        "/api/accounts/summary"
    ];

    public static void UseStudioSensitiveRouteAuthorization(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            string path = context.Request.Path.Value ?? string.Empty;
            if (administratorOnlyPaths.Any(value => path.Equals(value, StringComparison.OrdinalIgnoreCase))
                && !context.User.IsInRole("Administrator"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (TryParsePosSalePath(path, out Guid saleId, out string? operation))
            {
                StudioPosStore pos = context.RequestServices.GetRequiredService<StudioPosStore>();
                Sale? sale = pos.Get(saleId);
                if (sale is not null && Guid.TryParse(sale.ReferenceId, out Guid jobId))
                {
                    JobAccessService access = context.RequestServices.GetRequiredService<JobAccessService>();
                    string? permission = RequiredPosPermission(context.Request.Method, operation);
                    if (permission is not null && !access.HasPermission(context.User, jobId, permission))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return;
                    }
                }
            }

            await next();
        });
    }

    public static string? RequiredPosPermission(string method, string? operation)
    {
        if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(operation)) return "ViewPosTransactions";
        if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) && string.Equals(operation, "complete", StringComparison.OrdinalIgnoreCase)) return "UsePos";
        if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) && (string.Equals(operation, "void", StringComparison.OrdinalIgnoreCase) || string.Equals(operation, "refunds", StringComparison.OrdinalIgnoreCase))) return "RefundVoidPos";
        return null;
    }

    public static bool TryParsePosSalePath(string path, out Guid saleId, out string? operation)
    {
        saleId = Guid.Empty;
        operation = null;
        string[] segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 4
            || !segments[0].Equals("api", StringComparison.OrdinalIgnoreCase)
            || !segments[1].Equals("pos", StringComparison.OrdinalIgnoreCase)
            || !segments[2].Equals("sales", StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParse(segments[3], out saleId))
        {
            saleId = Guid.Empty;
            return false;
        }

        operation = segments.Length > 4 ? segments[4] : null;
        return true;
    }
}
