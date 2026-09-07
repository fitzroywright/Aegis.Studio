using System.Security.Claims;
using Aegis.Studio.Web.Notifications;
using Aegis.Studio.Web.Operations;

namespace Aegis.Studio.Web.Production;

public static class OperationalWorkEndpoints
{
    public static void MapStudioOperationalWork(this WebApplication app)
    {
        app.MapGet("/api/operations/summary", (OperationalWorkStore store) => Results.Ok(store.GetSummary())).RequireAuthorization();

        app.MapGet("/api/operations/activities", (Guid? jobId, string? status, string? assignedTo, ClaimsPrincipal user, OperationalWorkStore store, JobAccessService access) =>
        {
            IReadOnlyList<JobActivity> values = store.GetActivities(jobId, status, assignedTo)
                .Where(x => access.HasPermission(user, x.JobId, "ViewJob"))
                .ToList();
            return Results.Ok(values);
        }).RequireAuthorization();

        app.MapPost("/api/jobs/{jobId:guid}/operations/activities", (Guid jobId, JobActivityRequest request, ClaimsPrincipal user, OperationalWorkStore store, JobAccessService access, NotificationService notifications) =>
        {
            if (!access.HasPermission(user, jobId, "ManageProduction")) return Results.Forbid();
            try
            {
                JobActivity activity = store.CreateActivity(jobId, request, UserName(user));
                if (!string.IsNullOrWhiteSpace(activity.AssignedTo))
                {
                    access.EnsureAssignedMember(jobId, activity.AssignedTo);
                    notifications.PublishToUser(activity.AssignedTo, "JobAssigned", $"Activity assigned: {activity.Title}", $"You were assigned {activity.ActivityType} work.", "Job", jobId.ToString("D"));
                }
                return Results.Created($"/api/operations/activities/{activity.Id}", activity);
            }
            catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
        }).RequireAuthorization();

        app.MapPut("/api/operations/activities/{id:guid}", (Guid id, JobActivityRequest request, ClaimsPrincipal user, OperationalWorkStore store, JobAccessService access, NotificationService notifications) =>
        {
            JobActivity? current = store.GetActivities().FirstOrDefault(x => x.Id == id);
            if (current is null) return Results.NotFound();
            if (!access.HasPermission(user, current.JobId, "ManageProduction")) return Results.Forbid();
            try
            {
                JobActivity updated = store.UpdateActivity(id, request, UserName(user));
                if (updated.Status == "Blocked") notifications.PublishToUser(updated.AssignedTo ?? UserName(user), "JobStatusChanged", $"Blocked: {updated.Title}", updated.BlockedReason ?? "Production activity is blocked.", "Job", updated.JobId.ToString("D"));
                return Results.Ok(updated);
            }
            catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
        }).RequireAuthorization();

        app.MapGet("/api/operations/shoots", (Guid? jobId, ClaimsPrincipal user, OperationalWorkStore store, JobAccessService access) =>
            Results.Ok(store.GetShoots(jobId).Where(x => access.HasPermission(user, x.JobId, "ViewJob")).ToList()))
            .RequireAuthorization();

        app.MapPost("/api/jobs/{jobId:guid}/operations/shoots", (Guid jobId, ShootSessionRequest request, ClaimsPrincipal user, OperationalWorkStore store, JobAccessService access, NotificationService notifications) =>
        {
            if (!access.HasPermission(user, jobId, "ManageSchedule") && !access.HasPermission(user, jobId, "AssignStaff")) return Results.Forbid();
            try
            {
                ShootSession shoot = store.CreateShoot(jobId, request, UserName(user));
                access.EnsureAssignedMember(jobId, shoot.PhotographerUserName);
                if (!string.IsNullOrWhiteSpace(shoot.AssistantUserName)) access.EnsureAssignedMember(jobId, shoot.AssistantUserName);
                notifications.PublishToUser(shoot.PhotographerUserName, "JobAssigned", "Photography assignment", $"Call time {shoot.CallTimeUtc:u}{(shoot.Location is null ? string.Empty : $" at {shoot.Location}")}.", "Job", jobId.ToString("D"));
                return Results.Created($"/api/operations/shoots/{shoot.Id}", shoot);
            }
            catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
        }).RequireAuthorization();

        app.MapPut("/api/operations/shoots/{id:guid}", (Guid id, ShootSessionUpdateRequest request, ClaimsPrincipal user, OperationalWorkStore store, JobAccessService access, NotificationService notifications) =>
        {
            ShootSession? current = store.GetShoots().FirstOrDefault(x => x.Id == id);
            if (current is null) return Results.NotFound();
            string currentUser = UserName(user);
            bool photographer = string.Equals(current.PhotographerUserName, currentUser, StringComparison.OrdinalIgnoreCase);
            if (!photographer && !access.HasPermission(user, current.JobId, "ManageProduction") && !access.HasPermission(user, current.JobId, "ManageSchedule")) return Results.Forbid();
            try
            {
                ShootSession updated = store.UpdateShoot(id, request, currentUser);
                if (updated.ReshootRequired) notifications.PublishToUser(updated.PhotographerUserName, "JobStatusChanged", "Reshoot required", updated.CoverageNotes ?? "This job requires a reshoot.", "Job", updated.JobId.ToString("D"));
                if (updated.HandoffStatus == "HandedOff" && !string.IsNullOrWhiteSpace(updated.HandoffTo)) notifications.PublishToUser(updated.HandoffTo, "JobAssigned", "Files handed off to production", $"{updated.ImageCount} images are ready for production.", "Job", updated.JobId.ToString("D"));
                return Results.Ok(updated);
            }
            catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
        }).RequireAuthorization();

        app.MapGet("/api/jobs/{jobId:guid}/operations/audit", (Guid jobId, ClaimsPrincipal user, OperationalWorkStore store, JobAccessService access) =>
            access.HasPermission(user, jobId, "ViewJob") ? Results.Ok(store.GetAudit(jobId)) : Results.Forbid())
            .RequireAuthorization();
    }

    private static string UserName(ClaimsPrincipal user)
        => user.FindFirst("login_name")?.Value ?? user.Identity?.Name ?? "Studio";
}
