using System.Runtime.CompilerServices;
using System.Security.Claims;
using Aegis.Studio.Web.Email;
using Aegis.Studio.Web.Galleries;
using Aegis.Studio.Web.Notifications;
using Aegis.Studio.Web.Persistence;

namespace Aegis.Studio.Web.Operations;

public static class WorkspaceAccessEndpoints
{
    private static readonly ConditionalWeakTable<WebApplication, object> registrations = new();
    private static readonly object registrationLock = new();

    public static void MapWorkspaceAccess(this WebApplication app)
    {
        lock (registrationLock)
        {
            if (registrations.TryGetValue(app, out _)) return;
            registrations.Add(app, new object());
        }

        app.UseStudioTrustedProxyHeaders();
        app.MapStudioProductionHardening();
        app.UseStudioSameOriginWriteProtection();
        app.UseStudioSensitiveRouteAuthorization();
        app.UseMiddleware<WorkspaceAuthorizationMiddleware>();
        app.Services.GetRequiredService<JobEventNotifier>();
        app.MapStudioSecurityAdministration();
        app.MapGlobalOperationalRoles();
        app.MapLegacyMailboxImport();
        app.MapStudioWorkflowAutomation();
        app.MapStudioWorkflowOperations();
        app.MapStudioGalleries();
        app.MapStudioClientGalleryAccess();
        app.MapStudioGallerySelectionAdmin();
        app.MapStudioGalleryInternalFiles();
        app.MapStudioManagementAccountsPortal();
        app.MapStudioEscalations();

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
            WorkspacePermissionSet caller = access.GetPermissions(user, workspaceId);
            if (!user.IsInRole("Administrator") && !caller.ManageMembers && !caller.AdminWorkspace) return Results.Forbid();
            WorkspacePermissionSet requested = request.Permissions ?? WorkspacePermissionSet.ViewOnly;
            if (!user.IsInRole("Administrator") && !PermissionsWithin(requested, caller)) return Results.Forbid();
            WorkspaceMemberRequest normalized = request with { UserName = userName };
            return Results.Ok(access.SaveMember(workspaceId, normalized));
        }).RequireAuthorization();

        app.MapDelete("/api/workspaces/{workspaceId:guid}/members/{userName}", (Guid workspaceId, string userName, ClaimsPrincipal user, StudioDatabase database) =>
        {
            WorkspaceAccessService access = new(database);
            WorkspacePermissionSet caller = access.GetPermissions(user, workspaceId);
            if (!user.IsInRole("Administrator") && !caller.ManageMembers && !caller.AdminWorkspace) return Results.Forbid();
            WorkspaceMember? target = access.GetMembers(workspaceId).FirstOrDefault(x => string.Equals(x.UserName, userName, StringComparison.OrdinalIgnoreCase));
            if (target is not null && !user.IsInRole("Administrator") && !PermissionsWithin(target.Permissions, caller)) return Results.Forbid();
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

    public static bool PermissionsWithin(WorkspacePermissionSet requested, WorkspacePermissionSet caller)
        => (!requested.AccessWorkspace || caller.AccessWorkspace)
        && (!requested.View || caller.View)
        && (!requested.ReplyRequester || caller.ReplyRequester)
        && (!requested.ViewAttachments || caller.ViewAttachments)
        && (!requested.AddAttachments || caller.AddAttachments)
        && (!requested.CreateJob || caller.CreateJob)
        && (!requested.EditJob || caller.EditJob)
        && (!requested.ManageSchedule || caller.ManageSchedule)
        && (!requested.ManageProduction || caller.ManageProduction)
        && (!requested.NotifyMembers || caller.NotifyMembers)
        && (!requested.EditWorkspace || caller.EditWorkspace)
        && (!requested.ManageMembers || caller.ManageMembers)
        && (!requested.AdminWorkspace || caller.AdminWorkspace)
        && (!requested.SendAsSelf || caller.SendAsSelf);
}

public sealed record WorkspaceMemberNotificationRequest(string UserName,string Subject,string Body);
