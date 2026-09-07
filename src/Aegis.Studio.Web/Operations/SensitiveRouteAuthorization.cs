using Aegis.Pos.Core;
using Aegis.Studio.Domain.Bookings;
using Aegis.Studio.Web.Bookings;
using Aegis.Studio.Web.Pos;

namespace Aegis.Studio.Web.Operations;

public static class SensitiveRouteAuthorization
{
    private static readonly string[] administratorOnlyPaths =
    [
        "/api/email/status",
        "/api/email/unrouted",
        "/api/email/outbox",
        "/api/email/inbound",
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

            if (context.User.Identity?.IsAuthenticated == true && IsBookingPath(path))
            {
                if (!AuthorizeBookingRequest(context, path))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
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

    private static bool AuthorizeBookingRequest(HttpContext context, string path)
    {
        if (context.User.IsInRole("Administrator")) return true;

        GlobalOperationalRoleStore globalRoles = new(context.RequestServices.GetRequiredService<Persistence.StudioDatabase>());
        if (path.Equals("/api/bookings", StringComparison.OrdinalIgnoreCase)
            && context.Request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            return globalRoles.HasPermission(context.User, "ViewAllJobs");
        }

        if (!TryParseBookingPath(path, out Guid bookingId, out string? operation))
        {
            return true;
        }

        BookingStore bookings = context.RequestServices.GetRequiredService<BookingStore>();
        Booking? booking = bookings.Get(bookingId);
        if (booking is null) return true;

        JobAccessService jobAccess = context.RequestServices.GetRequiredService<JobAccessService>();
        if (context.Request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(operation))
        {
            return globalRoles.HasPermission(context.User, "ViewAllJobs") || jobAccess.HasPermission(context.User, booking.JobId, "ViewJob");
        }

        if (context.Request.Method.Equals("PUT", StringComparison.OrdinalIgnoreCase)
            && string.Equals(operation, "status", StringComparison.OrdinalIgnoreCase))
        {
            return jobAccess.HasPermission(context.User, booking.JobId, "ChangeStatus")
                || jobAccess.HasPermission(context.User, booking.JobId, "ManageSchedule");
        }

        if (context.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)
            && string.Equals(operation, "payment-confirmations", StringComparison.OrdinalIgnoreCase))
        {
            return globalRoles.HasPermission(context.User, "Billing")
                || jobAccess.HasPermission(context.User, booking.JobId, "RecordFinancialActivity");
        }

        return true;
    }

    private static bool IsBookingPath(string path)
        => path.Equals("/api/bookings", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/api/bookings/", StringComparison.OrdinalIgnoreCase);

    public static bool TryParseBookingPath(string path, out Guid bookingId, out string? operation)
    {
        bookingId = Guid.Empty;
        operation = null;
        string[] segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 3
            || !segments[0].Equals("api", StringComparison.OrdinalIgnoreCase)
            || !segments[1].Equals("bookings", StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParse(segments[2], out bookingId))
        {
            bookingId = Guid.Empty;
            return false;
        }

        operation = segments.Length > 3 ? segments[3] : null;
        return true;
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
