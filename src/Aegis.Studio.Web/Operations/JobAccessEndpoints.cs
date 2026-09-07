using System.Security.Claims;
using Aegis.Studio.Web.Notifications;

namespace Aegis.Studio.Web.Operations;

public static class JobAccessEndpoints
{
    public static void MapJobAccess(this WebApplication app)
    {
        app.MapGlobalOperationalRoles();

        app.MapGet("/api/jobs/{jobId:guid}/access", (Guid jobId, ClaimsPrincipal user, JobAccessService access) =>
        {
            if (access.GetWorkspaceId(jobId) is null) return Results.NotFound();
            return Results.Ok(new { permissions = access.GetPermissions(user, jobId) });
        }).RequireAuthorization();

        app.MapGet("/api/jobs/{jobId:guid}/members", (Guid jobId, ClaimsPrincipal user, JobAccessService access) =>
        {
            if (access.GetWorkspaceId(jobId) is null) return Results.NotFound();
            if (!access.HasPermission(user, jobId, "ManageJobMembers") && !access.HasPermission(user, jobId, "AdminJob") && !access.HasPermission(user, jobId, "NotifyJobMembers")) return Results.Forbid();
            return Results.Ok(access.GetMembers(jobId));
        }).RequireAuthorization();

        app.MapGet("/api/jobs/{jobId:guid}/notifiable-members", (Guid jobId, ClaimsPrincipal user, JobAccessService access, WorkspaceAccessService workspaceAccess) =>
        {
            Guid? workspaceId = access.GetWorkspaceId(jobId);
            if (!workspaceId.HasValue) return Results.NotFound();
            if (!access.HasPermission(user, jobId, "NotifyJobMembers") && !access.HasPermission(user, jobId, "AdminJob")) return Results.Forbid();

            Dictionary<string, NotifiableJobMember> members = new(StringComparer.OrdinalIgnoreCase);
            foreach (JobMember member in access.GetMembers(jobId).Where(x => x.IsActive))
            {
                members[member.UserName] = new NotifiableJobMember(member.UserName, member.DisplayName, "Job");
            }
            foreach (WorkspaceMember member in workspaceAccess.GetMembers(workspaceId.Value).Where(x => x.IsActive))
            {
                if (!members.ContainsKey(member.UserName))
                {
                    members[member.UserName] = new NotifiableJobMember(member.UserName, member.DisplayName, "Workspace");
                }
            }
            return Results.Ok(members.Values.OrderBy(x => x.DisplayName).ThenBy(x => x.UserName));
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

        app.MapPost("/api/jobs/{jobId:guid}/notifications", (Guid jobId, JobMemberNotificationRequest request, ClaimsPrincipal user, JobAccessService access, WorkspaceAccessService workspaceAccess, NotificationService notifications, StudioStore studio) =>
        {
            if (!access.HasPermission(user, jobId, "NotifyJobMembers") && !access.HasPermission(user, jobId, "AdminJob")) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Subject) || string.IsNullOrWhiteSpace(request.Body)) return Results.BadRequest(new { error = "Member, subject and message are required." });

            Guid? workspaceId = access.GetWorkspaceId(jobId);
            if (!workspaceId.HasValue) return Results.NotFound();

            JobMember? jobMember = access.GetMembers(jobId).FirstOrDefault(x => x.IsActive && string.Equals(x.UserName, request.UserName, StringComparison.OrdinalIgnoreCase));
            WorkspaceMember? workspaceMember = workspaceAccess.GetMembers(workspaceId.Value).FirstOrDefault(x => x.IsActive && string.Equals(x.UserName, request.UserName, StringComparison.OrdinalIgnoreCase));
            string? recipientUserName = jobMember?.UserName ?? workspaceMember?.UserName;
            string? recipientDisplayName = jobMember?.DisplayName ?? workspaceMember?.DisplayName;
            if (recipientUserName is null) return Results.BadRequest(new { error = "The selected user is not an active member of this job or its workspace." });

            StudioJob? job = studio.GetJob(jobId);
            if (job is null) return Results.NotFound();
            notifications.PublishToUser(recipientUserName, "JobNotification", request.Subject.Trim(), request.Body.Trim(), "Job", jobId.ToString("D"));
            return Results.Ok(new { sent = true, job = job.Title, member = recipientDisplayName ?? recipientUserName });
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
public sealed record NotifiableJobMember(string UserName,string DisplayName,string Source);
