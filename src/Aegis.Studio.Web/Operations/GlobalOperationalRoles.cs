using System.Security.Claims;
using Aegis.Studio.Web.Persistence;
using Microsoft.AspNetCore.Authorization;
using Npgsql;

namespace Aegis.Studio.Web.Operations;

public sealed class GlobalOperationalRoleStore
{
    public const string FrontDeskRoleName = "Front Desk";

    private readonly StudioDatabase database;

    public GlobalOperationalRoleStore(StudioDatabase database)
    {
        this.database = database;
    }

    public void EnsureSchema()
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            create table if not exists studio_global_operational_roles
            (
                id uuid primary key,
                name text not null unique,
                description text null,
                view_all_jobs boolean not null default false,
                reply_to_clients boolean not null default false,
                notify_job_members boolean not null default false,
                billing boolean not null default false,
                view_client_attachments boolean not null default false,
                add_client_attachments boolean not null default false,
                is_active boolean not null default true,
                created_at_utc timestamptz not null,
                updated_at_utc timestamptz not null
            );

            create table if not exists studio_global_operational_role_members
            (
                role_id uuid not null references studio_global_operational_roles(id) on delete cascade,
                user_name text not null,
                display_name text not null,
                is_active boolean not null default true,
                created_at_utc timestamptz not null,
                updated_at_utc timestamptz not null,
                primary key(role_id,user_name)
            );

            insert into studio_global_operational_roles
            (id,name,description,view_all_jobs,reply_to_clients,notify_job_members,billing,view_client_attachments,add_client_attachments,is_active,created_at_utc,updated_at_utc)
            values
            ('f40f8c48-233b-49f4-a1d3-5ba85d6fc001','Front Desk','Global operational role for reception/front-desk staff. Provides cross-workspace job visibility, client communication, job-member notifications, attachments and billing without workspace administration.',true,true,true,true,true,true,true,now(),now())
            on conflict(name) do update set
                description=excluded.description,
                view_all_jobs=true,
                reply_to_clients=true,
                notify_job_members=true,
                billing=true,
                view_client_attachments=true,
                add_client_attachments=true,
                is_active=true,
                updated_at_utc=now();
            """;
        command.ExecuteNonQuery();
    }

    public bool HasPermission(ClaimsPrincipal user, string permission)
    {
        if (user.IsInRole("Administrator"))
        {
            return true;
        }

        string userName = GetUserName(user);
        GlobalOperationalPermissions permissions = GetEffectivePermissions(userName);
        return permission switch
        {
            "ViewAllJobs" => permissions.ViewAllJobs,
            "ReplyToClients" => permissions.ReplyToClients,
            "NotifyJobMembers" => permissions.NotifyJobMembers,
            "Billing" => permissions.Billing,
            "ViewClientAttachments" => permissions.ViewClientAttachments,
            "AddClientAttachments" => permissions.AddClientAttachments,
            _ => false
        };
    }

    public GlobalOperationalPermissions GetEffectivePermissions(string userName)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select
                coalesce(bool_or(r.view_all_jobs),false),
                coalesce(bool_or(r.reply_to_clients),false),
                coalesce(bool_or(r.notify_job_members),false),
                coalesce(bool_or(r.billing),false),
                coalesce(bool_or(r.view_client_attachments),false),
                coalesce(bool_or(r.add_client_attachments),false)
            from studio_global_operational_roles r
            join studio_global_operational_role_members m on m.role_id=r.id
            where r.is_active and m.is_active and lower(m.user_name)=lower($1)
            """;
        command.Parameters.AddWithValue(userName);
        using NpgsqlDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return GlobalOperationalPermissions.None;
        return new GlobalOperationalPermissions(reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5));
    }

    public IReadOnlyList<GlobalOperationalRole> GetRoles()
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id,name,description,view_all_jobs,reply_to_clients,notify_job_members,billing,view_client_attachments,add_client_attachments,is_active from studio_global_operational_roles order by name";
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<GlobalOperationalRole> roles = [];
        while (reader.Read())
        {
            roles.Add(new GlobalOperationalRole(
                reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                new GlobalOperationalPermissions(reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5), reader.GetBoolean(6), reader.GetBoolean(7), reader.GetBoolean(8)),
                reader.GetBoolean(9)));
        }
        return roles;
    }

    public IReadOnlyList<GlobalOperationalRoleMember> GetMembers(Guid roleId)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select role_id,user_name,display_name,is_active,created_at_utc,updated_at_utc from studio_global_operational_role_members where role_id=$1 order by display_name,user_name";
        command.Parameters.AddWithValue(roleId);
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<GlobalOperationalRoleMember> members = [];
        while (reader.Read())
        {
            members.Add(new GlobalOperationalRoleMember(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetFieldValue<DateTimeOffset>(4), reader.GetFieldValue<DateTimeOffset>(5)));
        }
        return members;
    }

    public GlobalOperationalRoleMember SaveMember(Guid roleId, GlobalOperationalRoleMemberRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.UserName)) throw new InvalidOperationException("User name is required.");
        if (!GetRoles().Any(x => x.Id == roleId && x.IsActive)) throw new KeyNotFoundException("Global operational role was not found.");

        string userName = request.UserName.Trim();
        string displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? userName : request.DisplayName.Trim();
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            insert into studio_global_operational_role_members(role_id,user_name,display_name,is_active,created_at_utc,updated_at_utc)
            values($1,$2,$3,$4,now(),now())
            on conflict(role_id,user_name) do update set display_name=excluded.display_name,is_active=excluded.is_active,updated_at_utc=now()
            """;
        command.Parameters.AddWithValue(roleId);
        command.Parameters.AddWithValue(userName);
        command.Parameters.AddWithValue(displayName);
        command.Parameters.AddWithValue(request.IsActive);
        command.ExecuteNonQuery();
        return GetMembers(roleId).First(x => string.Equals(x.UserName, userName, StringComparison.OrdinalIgnoreCase));
    }

    public void RemoveMember(Guid roleId, string userName)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "delete from studio_global_operational_role_members where role_id=$1 and lower(user_name)=lower($2)";
        command.Parameters.AddWithValue(roleId);
        command.Parameters.AddWithValue(userName.Trim());
        command.ExecuteNonQuery();
    }

    private static string GetUserName(ClaimsPrincipal user)
        => user.FindFirstValue("login_name") ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.Identity?.Name ?? throw new InvalidOperationException("Authenticated user has no login identifier.");
}

public static class GlobalOperationalRoleEndpoints
{
    public static void MapGlobalOperationalRoles(this WebApplication app)
    {
        GlobalOperationalRoleStore roles = app.Services.GetRequiredService<GlobalOperationalRoleStore>();
        roles.EnsureSchema();

        RouteGroupBuilder group = app.MapGroup("/api/security/global-roles").RequireAuthorization(new AuthorizeAttribute { Roles = "Administrator" });
        group.MapGet("/", () => Results.Ok(roles.GetRoles()));
        group.MapGet("/{roleId:guid}/members", (Guid roleId) => Results.Ok(roles.GetMembers(roleId)));
        group.MapPut("/{roleId:guid}/members/{userName}", (Guid roleId, string userName, GlobalOperationalRoleMemberRequest request) =>
        {
            try { return Results.Ok(roles.SaveMember(roleId, request with { UserName = userName })); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });
        group.MapDelete("/{roleId:guid}/members/{userName}", (Guid roleId, string userName) => { roles.RemoveMember(roleId, userName); return Results.NoContent(); });

        app.MapGet("/api/security/global-role-me", (ClaimsPrincipal user) => Results.Ok(new
        {
            frontDesk = roles.HasPermission(user, "ViewAllJobs"),
            permissions = roles.GetEffectivePermissions(GetUserName(user))
        })).RequireAuthorization();
    }

    private static string GetUserName(ClaimsPrincipal user)
        => user.FindFirstValue("login_name") ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.Identity?.Name ?? string.Empty;
}

public sealed record GlobalOperationalPermissions(bool ViewAllJobs,bool ReplyToClients,bool NotifyJobMembers,bool Billing,bool ViewClientAttachments,bool AddClientAttachments)
{
    public static GlobalOperationalPermissions None => new(false,false,false,false,false,false);
}

public sealed record GlobalOperationalRole(Guid Id,string Name,string? Description,GlobalOperationalPermissions Permissions,bool IsActive);
public sealed record GlobalOperationalRoleMember(Guid RoleId,string UserName,string DisplayName,bool IsActive,DateTimeOffset CreatedAtUtc,DateTimeOffset UpdatedAtUtc);
public sealed record GlobalOperationalRoleMemberRequest(string UserName,string? DisplayName,bool IsActive = true);
