using System.Text.Json;
using Aegis.Studio.Web.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace Aegis.Studio.Web.Operations;

public static class SecurityAdministrationEndpoints
{
    public static void MapStudioSecurityAdministration(this WebApplication app)
    {
        StudioRoleStore roles = new(app.Services.GetRequiredService<StudioDatabase>());
        roles.EnsureSchema();

        RouteGroupBuilder group = app.MapGroup("/api/security").RequireAuthorization(new AuthorizeAttribute { Roles = "Administrator" });
        group.MapGet("/roles", () => Results.Ok(roles.GetAll()));
        group.MapPost("/roles", (StudioRoleRequest request) =>
        {
            try { return Results.Ok(roles.Save(null, request)); }
            catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
        });
        group.MapPut("/roles/{id:guid}", (Guid id, StudioRoleRequest request) =>
        {
            try { return Results.Ok(roles.Save(id, request)); }
            catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
        });
        group.MapDelete("/roles/{id:guid}", (Guid id) => { roles.Delete(id); return Results.NoContent(); });

        group.MapPut("/workspaces/{workspaceId:guid}/members/{userName}/role/{roleId:guid}", (Guid workspaceId, string userName, Guid roleId, WorkspaceAccessService access) =>
        {
            StudioRole role = roles.Get(roleId) ?? throw new KeyNotFoundException("Role was not found.");
            if (!role.Scope.Equals("Workspace", StringComparison.OrdinalIgnoreCase)) return Results.BadRequest(new { error = "A Workspace role is required." });
            WorkspaceMember? member = access.GetMembers(workspaceId).FirstOrDefault(x => x.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase));
            WorkspacePermissionSet permissions = JsonSerializer.Deserialize<WorkspacePermissionSet>(role.PermissionsJson) ?? WorkspacePermissionSet.None;
            WorkspaceMember saved = access.SaveMember(workspaceId, new WorkspaceMemberRequest(userName, member?.DisplayName ?? userName, true, permissions));
            roles.SetWorkspaceRole(workspaceId, userName, role.Id);
            return Results.Ok(new { member = saved, role });
        });

        group.MapPut("/jobs/{jobId:guid}/members/{userName}/role/{roleId:guid}", (Guid jobId, string userName, Guid roleId, JobAccessService access) =>
        {
            StudioRole role = roles.Get(roleId) ?? throw new KeyNotFoundException("Role was not found.");
            if (!role.Scope.Equals("Job", StringComparison.OrdinalIgnoreCase)) return Results.BadRequest(new { error = "A Job role is required." });
            JobMember? member = access.GetMembers(jobId).FirstOrDefault(x => x.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase));
            JobPermissionSet permissions = JsonSerializer.Deserialize<JobPermissionSet>(role.PermissionsJson) ?? JobPermissionSet.None;
            JobMember saved = access.SaveMember(jobId, new JobMemberRequest(userName, member?.DisplayName ?? userName, true, false, permissions));
            roles.SetJobRole(jobId, userName, role.Id);
            return Results.Ok(new { member = saved, role });
        });

        group.MapGet("/authorization-audit", () => Results.Ok(AuthorizationAuditService.Audit(app.DataSources.SelectMany(x => x.Endpoints))));

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            AuthorizationAuditReport report = AuthorizationAuditService.Audit(app.DataSources.SelectMany(x => x.Endpoints));
            if (report.UnapprovedAnonymous.Count > 0) app.Logger.LogCritical("Authorization audit found unapproved anonymous API endpoints: {Endpoints}", string.Join(", ", report.UnapprovedAnonymous));
            else app.Logger.LogInformation("Authorization audit completed: {ApiEndpoints} API endpoints checked; fallback authentication is enabled; {ApprovedAnonymous} approved anonymous endpoints.", report.ApiEndpointCount, report.ApprovedAnonymous.Count);
        });
    }
}

public sealed class StudioRoleStore
{
    private readonly StudioDatabase database;
    public StudioRoleStore(StudioDatabase database) => this.database = database;

    public void EnsureSchema()
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            create table if not exists studio_security_roles
            (
                id uuid primary key,
                name text not null,
                scope text not null check(scope in ('Workspace','Job')),
                description text null,
                permissions_json jsonb not null,
                is_active boolean not null default true,
                created_at_utc timestamptz not null,
                updated_at_utc timestamptz not null
            );
            create unique index if not exists ux_studio_security_roles_name_scope on studio_security_roles(lower(name),scope);
            alter table studio_workspace_members add column if not exists role_id uuid null references studio_security_roles(id) on delete set null;
            alter table studio_job_members add column if not exists role_id uuid null references studio_security_roles(id) on delete set null;
            """;
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<StudioRole> GetAll()
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id,name,scope,description,permissions_json::text,is_active,created_at_utc,updated_at_utc from studio_security_roles order by scope,name";
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<StudioRole> values = [];
        while (reader.Read()) values.Add(Read(reader));
        return values;
    }

    public StudioRole? Get(Guid id) => GetAll().FirstOrDefault(x => x.Id == id);

    public StudioRole Save(Guid? id, StudioRoleRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw new InvalidOperationException("Role name is required.");
        string scope = request.Scope?.Trim() ?? string.Empty;
        if (scope != "Workspace" && scope != "Job") throw new InvalidOperationException("Role scope must be Workspace or Job.");
        ValidatePermissions(scope, request.PermissionsJson);
        Guid roleId = id ?? Guid.NewGuid();
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlTransaction transaction = connection.BeginTransaction();
        using (NpgsqlCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                insert into studio_security_roles(id,name,scope,description,permissions_json,is_active,created_at_utc,updated_at_utc)
                values($1,$2,$3,$4,$5::jsonb,$6,now(),now())
                on conflict(id) do update set name=excluded.name,scope=excluded.scope,description=excluded.description,permissions_json=excluded.permissions_json,is_active=excluded.is_active,updated_at_utc=now()
                """;
            command.Parameters.AddWithValue(roleId); command.Parameters.AddWithValue(request.Name.Trim()); command.Parameters.AddWithValue(scope); command.Parameters.AddWithValue((object?)request.Description?.Trim() ?? DBNull.Value); command.Parameters.AddWithValue(request.PermissionsJson); command.Parameters.AddWithValue(request.IsActive); command.ExecuteNonQuery();
        }
        Propagate(connection, transaction, roleId, scope, request.PermissionsJson);
        transaction.Commit();
        return Get(roleId)!;
    }

    public void Delete(Guid id)
    {
        using NpgsqlConnection connection = database.OpenConnection(); using NpgsqlCommand command = connection.CreateCommand(); command.CommandText = "delete from studio_security_roles where id=$1"; command.Parameters.AddWithValue(id); command.ExecuteNonQuery();
    }

    public void SetWorkspaceRole(Guid workspaceId, string userName, Guid roleId) => SetRole("studio_workspace_members", "workspace_id", workspaceId, userName, roleId);
    public void SetJobRole(Guid jobId, string userName, Guid roleId) => SetRole("studio_job_members", "job_id", jobId, userName, roleId);

    private void SetRole(string table, string idColumn, Guid id, string userName, Guid roleId)
    {
        using NpgsqlConnection connection = database.OpenConnection(); using NpgsqlCommand command = connection.CreateCommand(); command.CommandText = $"update {table} set role_id=$3,updated_at_utc=now() where {idColumn}=$1 and lower(user_name)=lower($2)"; command.Parameters.AddWithValue(id); command.Parameters.AddWithValue(userName); command.Parameters.AddWithValue(roleId); command.ExecuteNonQuery();
    }

    private static void ValidatePermissions(string scope, string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new InvalidOperationException("Permissions JSON is required.");
        if (scope == "Workspace") _ = JsonSerializer.Deserialize<WorkspacePermissionSet>(json) ?? throw new InvalidOperationException("Invalid Workspace permission set.");
        else _ = JsonSerializer.Deserialize<JobPermissionSet>(json) ?? throw new InvalidOperationException("Invalid Job permission set.");
    }

    private static void Propagate(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid roleId, string scope, string json)
    {
        if (scope == "Workspace")
        {
            WorkspacePermissionSet p = JsonSerializer.Deserialize<WorkspacePermissionSet>(json)!; using NpgsqlCommand command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "update studio_workspace_members set access_workspace=$2,can_view=$3,reply_requester=$4,view_attachments=$5,add_attachments=$6,create_job=$7,edit_job=$8,manage_schedule=$9,manage_production=$10,notify_members=$11,edit_workspace=$12,manage_members=$13,admin_workspace=$14,updated_at_utc=now() where role_id=$1";
            object[] values=[roleId,p.AccessWorkspace,p.View,p.ReplyRequester,p.ViewAttachments,p.AddAttachments,p.CreateJob,p.EditJob,p.ManageSchedule,p.ManageProduction,p.NotifyMembers,p.EditWorkspace,p.ManageMembers,p.AdminWorkspace]; foreach(object value in values) command.Parameters.AddWithValue(value); command.ExecuteNonQuery();
        }
        else
        {
            JobPermissionSet p = JsonSerializer.Deserialize<JobPermissionSet>(json)!; using NpgsqlCommand command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "update studio_job_members set inherit_workspace_permissions=false,access_job=$2,view_job=$3,view_conversation=$4,reply_requester=$5,view_attachments=$6,add_attachments=$7,edit_job=$8,change_status=$9,assign_staff=$10,manage_schedule=$11,manage_production=$12,view_financials=$13,record_financial_activity=$14,use_pos=$15,view_pos_transactions=$16,refund_void_pos=$17,notify_job_members=$18,manage_job_members=$19,admin_job=$20,updated_at_utc=now() where role_id=$1";
            object[] values=[roleId,p.AccessJob,p.ViewJob,p.ViewConversation,p.ReplyRequester,p.ViewAttachments,p.AddAttachments,p.EditJob,p.ChangeStatus,p.AssignStaff,p.ManageSchedule,p.ManageProduction,p.ViewFinancials,p.RecordFinancialActivity,p.UsePos,p.ViewPosTransactions,p.RefundVoidPos,p.NotifyJobMembers,p.ManageJobMembers,p.AdminJob]; foreach(object value in values) command.Parameters.AddWithValue(value); command.ExecuteNonQuery();
        }
    }

    private static StudioRole Read(NpgsqlDataReader r) => new(r.GetGuid(0),r.GetString(1),r.GetString(2),r.IsDBNull(3)?null:r.GetString(3),r.GetString(4),r.GetBoolean(5),r.GetFieldValue<DateTimeOffset>(6),r.GetFieldValue<DateTimeOffset>(7));
}

public static class AuthorizationAuditService
{
    public static AuthorizationAuditReport Audit(IEnumerable<Endpoint> endpoints)
    {
        List<string> approvedAnonymous=[]; List<string> unapprovedAnonymous=[]; int apiCount=0;
        foreach (RouteEndpoint endpoint in endpoints.OfType<RouteEndpoint>())
        {
            string route = "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/');
            if (!route.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) continue;
            apiCount++;
            bool anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            if (!anonymous) continue;
            if (AuthorizationAuditPolicy.IsApprovedAnonymousPath(route)) approvedAnonymous.Add(route); else unapprovedAnonymous.Add(route);
        }
        return new AuthorizationAuditReport(apiCount, approvedAnonymous.Distinct().Order().ToArray(), unapprovedAnonymous.Distinct().Order().ToArray(), "All non-anonymous API endpoints are protected by the application's authenticated fallback policy; resource APIs additionally enforce Workspace/Job permissions.");
    }
}

public static class AuthorizationAuditPolicy
{
    public static bool IsApprovedAnonymousPath(string route)
        => route.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase)
        || route.Equals("/api/bookings", StringComparison.OrdinalIgnoreCase)
        || route.StartsWith("/api/bookings/{id:guid}", StringComparison.OrdinalIgnoreCase);
}

public sealed record StudioRole(Guid Id,string Name,string Scope,string? Description,string PermissionsJson,bool IsActive,DateTimeOffset CreatedAtUtc,DateTimeOffset UpdatedAtUtc);
public sealed record StudioRoleRequest(string Name,string Scope,string? Description,string PermissionsJson,bool IsActive=true);
public sealed record AuthorizationAuditReport(int ApiEndpointCount,IReadOnlyList<string> ApprovedAnonymous,IReadOnlyList<string> UnapprovedAnonymous,string EnforcementModel);
