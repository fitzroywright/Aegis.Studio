using System.Security.Claims;
using Aegis.Studio.Web.Notifications;
using Aegis.Studio.Web.Persistence;

namespace Aegis.Studio.Web.Operations;

public static class WorkspaceAccessEndpoints
{
    private const string RegistrationKey = "Aegis.Studio.WorkspaceAccessEndpoints.Registered";

    public static void MapWorkspaceAccess(this WebApplication app)
    {
        if (app.Properties.ContainsKey(RegistrationKey))
        {
            return;
        }

        app.Properties[RegistrationKey] = true;
        app.UseMiddleware<WorkspaceAuthorizationMiddleware>();

        app.MapGet("/api/workspace-access", (ClaimsPrincipal user, StudioDatabase database, WorkspaceStore workspaces) =>
        {
            WorkspaceAccessService access = new(database);
            IReadOnlyList<Guid> ids = access.GetAccessibleWorkspaceIds(user);
            IReadOnlyList<StudioWorkspaceDefinition> values = workspaces.GetAll().Where(x => ids.Contains(x.Id)).ToList();
            return Results.Ok(values.Select(x => new { workspace = x, permissions = access.GetPermissions(user, x.Id) }));
        }).RequireAuthorization();

        app.MapGet("/api/workspaces/{workspaceId:guid}", (Guid workspaceId, ClaimsPrincipal user, StudioDatabase database, WorkspaceStore workspaces) =>
        {
            WorkspaceAccessService access = new(database);
            if (!access.HasPermission(user, workspaceId, "AccessWorkspace")) return Results.Forbid();
            return workspaces.Get(workspaceId) is StudioWorkspaceDefinition workspace ? Results.Ok(workspace) : Results.NotFound();
        }).RequireAuthorization();

        app.MapPut("/api/workspaces/{workspaceId:guid}", (Guid workspaceId, SaveWorkspaceRequest request, ClaimsPrincipal user, StudioDatabase database, WorkspaceStore workspaces) =>
        {
            WorkspaceAccessService access = new(database);
            if (!access.HasPermission(user, workspaceId, "EditWorkspace") && !access.HasPermission(user, workspaceId, "AdminWorkspace")) return Results.Forbid();
            try { return Results.Ok(workspaces.Update(workspaceId, request)); } catch (KeyNotFoundException) { return Results.NotFound(); }
        }).RequireAuthorization();

        app.MapGet("/api/workspaces/{workspaceId:guid}/permissions", (Guid workspaceId, ClaimsPrincipal user, StudioDatabase database) =>
        {
            WorkspaceAccessService access = new(database);
            return Results.Ok(access.GetPermissions(user, workspaceId));
        }).RequireAuthorization();

        app.MapGet("/api/workspaces/{workspaceId:guid}/members", (Guid workspaceId, ClaimsPrincipal user, StudioDatabase database) =>
        {
            WorkspaceAccessService access = new(database);
            if (!access.HasPermission(user, workspaceId, "ManageMembers") && !access.HasPermission(user, workspaceId, "AdminWorkspace")) return Results.Forbid();
            return Results.Ok(access.GetMembers(workspaceId));
        }).RequireAuthorization();

        app.MapPut("/api/workspaces/{workspaceId:guid}/members/{userName}", (Guid workspaceId, string userName, WorkspaceMemberRequest request, ClaimsPrincipal user, StudioDatabase database) =>
        {
            WorkspaceAccessService access = new(database);
            if (!access.HasPermission(user, workspaceId, "ManageMembers") && !access.HasPermission(user, workspaceId, "AdminWorkspace")) return Results.Forbid();
            WorkspaceMemberRequest normalized = request with { UserName = userName };
            return Results.Ok(access.SaveMember(workspaceId, normalized));
        }).RequireAuthorization();

        app.MapDelete("/api/workspaces/{workspaceId:guid}/members/{userName}", (Guid workspaceId, string userName, ClaimsPrincipal user, StudioDatabase database) =>
        {
            WorkspaceAccessService access = new(database);
            if (!access.HasPermission(user, workspaceId, "ManageMembers") && !access.HasPermission(user, workspaceId, "AdminWorkspace")) return Results.Forbid();
            access.RemoveMember(workspaceId, userName);
            return Results.NoContent();
        }).RequireAuthorization();

        app.MapPost("/api/workspaces/{workspaceId:guid}/notifications", (Guid workspaceId, WorkspaceMemberNotificationRequest request, ClaimsPrincipal user, StudioDatabase database, NotificationService notifications, WorkspaceStore workspaces) =>
        {
            WorkspaceAccessService access = new(database);
            if (!access.HasPermission(user, workspaceId, "NotifyMembers") && !access.HasPermission(user, workspaceId, "AdminWorkspace")) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Subject) || string.IsNullOrWhiteSpace(request.Body)) return Results.BadRequest(new { error = "Member, subject and message are required." });
            WorkspaceMember? member = access.GetMembers(workspaceId).FirstOrDefault(x => x.IsActive && string.Equals(x.UserName, request.UserName, StringComparison.OrdinalIgnoreCase));
            if (member is null) return Results.BadRequest(new { error = "The selected user is not an active member of this workspace." });
            StudioWorkspaceDefinition? workspace = workspaces.Get(workspaceId);
            notifications.PublishToUser(member.UserName, "WorkspaceNotification", request.Subject.Trim(), request.Body.Trim(), "Workspace", workspaceId.ToString("D"));
            return Results.Ok(new { sent = true, workspace = workspace?.Name, member = member.DisplayName });
        }).RequireAuthorization();
    }
}

public sealed record WorkspaceMemberNotificationRequest(string UserName,string Subject,string Body);
