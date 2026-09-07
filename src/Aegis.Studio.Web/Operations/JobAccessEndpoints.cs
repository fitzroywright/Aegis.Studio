using System.Security.Claims;
using Aegis.Studio.Web.Notifications;

namespace Aegis.Studio.Web.Operations;

public static class JobAccessEndpoints
{
    public static void MapJobAccess(this WebApplication app)
    {
        app.MapGet("/api/jobs/{jobId:guid}/access", (Guid jobId, ClaimsPrincipal user, JobAccessService access) =>
        {
            if (access.GetWorkspaceId(jobId) is null) return Results.NotFound();
            return Results.Ok(new { permissions = access.GetPermissions(user, jobId) });
        }).RequireAuthorization();

        app.MapGet("/api/jobs/{jobId:guid}/members", (Guid jobId, ClaimsPrincipal user, JobAccessService access) =>
        {
            if (access.GetWorkspaceId(jobId) is null) return Results.NotFound();
            if (!access.HasPermission(user, jobId, "ManageJobMembers") && !access.HasPermission(user, jobId, "AdminJob")) return Results.Forbid();
            return Results.Ok(access.GetMembers(jobId));
        }).RequireAuthorization();

        app.MapPut("/api/jobs/{jobId:guid}/members/{userName}", (Guid jobId, string userName, JobMemberRequest request, ClaimsPrincipal user, JobAccessService access) =>
        {
            if (!access.HasPermission(user, jobId, "ManageJobMembers") && !access.HasPermission(user, jobId, "AdminJob")) return Results.Forbid();
            JobMemberRequest normalized = request with { UserName = userName };
            return Results.Ok(access.SaveMember(jobId, normalized));
        }).RequireAuthorization();

        app.MapDelete("/api/jobs/{jobId:guid}/members/{userName}", (Guid jobId, string userName, ClaimsPrincipal user, JobAccessService access) =>
        {
            if (!access.HasPermission(user, jobId, "ManageJobMembers") && !access.HasPermission(user, jobId, "AdminJob")) return Results.Forbid();
            access.RemoveMember(jobId, userName);
            return Results.NoContent();
        }).RequireAuthorization();

        app.MapPost("/api/jobs/{jobId:guid}/notifications", (Guid jobId, JobMemberNotificationRequest request, ClaimsPrincipal user, JobAccessService access, NotificationService notifications, StudioStore studio) =>
        {
            if (!access.HasPermission(user, jobId, "NotifyJobMembers") && !access.HasPermission(user, jobId, "AdminJob")) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Subject) || string.IsNullOrWhiteSpace(request.Body)) return Results.BadRequest(new { error = "Member, subject and message are required." });

            JobMember? member = access.GetMembers(jobId).FirstOrDefault(x => x.IsActive && string.Equals(x.UserName, request.UserName, StringComparison.OrdinalIgnoreCase));
            if (member is null) return Results.BadRequest(new { error = "The selected user is not an active explicit member of this job." });

            StudioJob? job = studio.GetJob(jobId);
            if (job is null) return Results.NotFound();
            notifications.PublishToUser(member.UserName, "JobNotification", request.Subject.Trim(), request.Body.Trim(), "Job", jobId.ToString("D"));
            return Results.Ok(new { sent = true, job = job.Title, member = member.DisplayName });
        }).RequireAuthorization();

        app.MapPost("/api/jobs/{jobId:guid}/financial-events", (Guid jobId, AddFinancialEventRequest request, ClaimsPrincipal user, JobAccessService access, StudioStore studio) =>
        {
            if (!access.HasPermission(user, jobId, "RecordFinancialActivity")) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(request.Kind) || request.Amount == 0) return Results.BadRequest(new { error = "Financial event type and a non-zero amount are required." });
            try { return Results.Ok(studio.AddFinancialEvent(jobId, request)); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        }).RequireAuthorization();
    }
}

public sealed record JobMemberNotificationRequest(string UserName,string Subject,string Body);
