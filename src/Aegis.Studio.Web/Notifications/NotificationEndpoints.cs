using System.Security.Claims;

namespace Aegis.Studio.Web.Notifications;

public static class NotificationEndpoints
{
    public static void MapStudioNotifications(this WebApplication app)
    {
        app.MapGet("/api/notifications", (ClaimsPrincipal user, int? count, NotificationService notifications) =>
        {
            string userName = user.FindFirstValue("login_name") ?? user.Identity?.Name ?? string.Empty;
            return Results.Ok(notifications.GetForUser(userName, count ?? 100));
        }).RequireAuthorization();

        app.MapPost("/api/notifications/{id:guid}/read", (Guid id, ClaimsPrincipal user, NotificationService notifications) =>
        {
            string userName = user.FindFirstValue("login_name") ?? user.Identity?.Name ?? string.Empty;
            notifications.MarkRead(id, userName);
            return Results.NoContent();
        }).RequireAuthorization();
    }
}
