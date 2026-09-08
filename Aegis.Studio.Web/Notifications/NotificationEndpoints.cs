using System.Security.Claims;
using Aegis.Studio.Web.Email;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;

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

        app.MapGet("/api/jobs/{jobId:guid}/requester-notifications", (
            Guid jobId,
            ClaimsPrincipal user,
            StudioDatabase database,
            NotificationService notifications,
            StudioStore studio,
            WorkspaceAccessService workspaceAccess,
            JobAccessService jobAccess,
            OutboundEmailStore outbox) =>
        {
            if (!jobAccess.HasPermission(user, jobId, "ViewJob")) return Results.Forbid();
            JobEventNotifier notifier = new(database, notifications, studio, workspaceAccess, jobAccess, outbox);
            return Results.Ok(notifier.GetRequesterPreferences(jobId));
        }).RequireAuthorization();

        app.MapPut("/api/jobs/{jobId:guid}/requester-notifications", (
            Guid jobId,
            RequesterNotificationPreferences request,
            ClaimsPrincipal user,
            StudioDatabase database,
            NotificationService notifications,
            StudioStore studio,
            WorkspaceAccessService workspaceAccess,
            JobAccessService jobAccess,
            OutboundEmailStore outbox) =>
        {
            if (!jobAccess.HasPermission(user, jobId, "EditJob") && !jobAccess.HasPermission(user, jobId, "AdminJob")) return Results.Forbid();
            try
            {
                JobEventNotifier notifier = new(database, notifications, studio, workspaceAccess, jobAccess, outbox);
                return Results.Ok(notifier.SaveRequesterPreferences(jobId, request));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        }).RequireAuthorization();
    }
}
