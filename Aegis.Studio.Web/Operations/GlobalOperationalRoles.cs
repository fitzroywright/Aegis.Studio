using Common.Security.Hosting;
using Common.Security.Integration;
using System.Security.Claims;
using Aegis.Studio.Web.Persistence;
using Common.Security.Abstractions;
using Common.Security.Authorization;
using Microsoft.AspNetCore.Authorization;
using Npgsql;

namespace Aegis.Studio.Web.Operations;

public static class StudioRoleNames
{
    public const string User = "User";
    public const string FrontDesk = "Front Desk";
    public const string OperationsManager = "Operations Manager";
    public const string Administrator = "Administrator";
    public const string FinanceManager = "Finance Manager";
    public const string ProductionManager = "Production Manager";
}

public static class StudioPermissions
{
    public const string Access = "studio.access";
    public const string ViewAllJobs = "studio.jobs.view_all";
    public const string ReplyToClients = "studio.clients.reply";
    public const string NotifyJobMembers = "studio.jobs.notify_members";
    public const string Billing = "studio.billing.manage";
    public const string ViewClientAttachments = "studio.attachments.view";
    public const string AddClientAttachments = "studio.attachments.add";
    public const string ManageSecurity = "studio.security.manage";
    public const string DeleteMedia = "studio.media.delete";
}

public sealed class GlobalOperationalRoleStore : IAuthorizationStore
{
    private static readonly HashSet<string> SupportedPermissions = new(StringComparer.OrdinalIgnoreCase)
    {
        PermissionAuthorizer.AllPermissions,
        StudioPermissions.Access,
        StudioPermissions.ViewAllJobs,
        StudioPermissions.ReplyToClients,
        StudioPermissions.NotifyJobMembers,
        StudioPermissions.Billing,
        StudioPermissions.ViewClientAttachments,
        StudioPermissions.AddClientAttachments,
        StudioPermissions.ManageSecurity,
        StudioPermissions.DeleteMedia
    };
    private static readonly Guid UserRoleId = Guid.Parse("f40f8c48-233b-49f4-a1d3-5ba85d6fc002");
    private static readonly Guid FrontDeskRoleId = Guid.Parse("f40f8c48-233b-49f4-a1d3-5ba85d6fc001");
    private static readonly Guid AdministratorRoleId = Guid.Parse("f40f8c48-233b-49f4-a1d3-5ba85d6fc003");
    private static readonly Guid OperationsManagerRoleId = Guid.Parse("f40f8c48-233b-49f4-a1d3-5ba85d6fc004");

    private static readonly Guid FinanceManagerRoleId = Guid.Parse("f40f8c48-233b-49f4-a1d3-5ba85d6fc005");
    private static readonly Guid ProductionManagerRoleId = Guid.Parse("f40f8c48-233b-49f4-a1d3-5ba85d6fc006");
    private static readonly Guid MediaCustodianRoleId = Guid.Parse("f40f8c48-233b-49f4-a1d3-5ba85d6fc007");

    private readonly StudioDatabase database;

    public GlobalOperationalRoleStore(StudioDatabase database)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public void EnsureSchema(IEnumerable<string>? administratorGroups = null)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            create table if not exists studio_global_operational_roles
            (
                id uuid primary key,
                name text not null unique,
                description text null,
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

            create table if not exists studio_global_operational_role_permissions
            (
                role_id uuid not null references studio_global_operational_roles(id) on delete cascade,
                permission_key text not null,
                primary key(role_id,permission_key)
            );

            create table if not exists studio_directory_group_roles
            (
                group_name text not null,
                role_id uuid not null references studio_global_operational_roles(id) on delete cascade,
                primary key(group_name,role_id)
            );
            """;
        command.ExecuteNonQuery();

        var created = new HashSet<Guid>();
        if (EnsureRole(UserRoleId, StudioRoleNames.User, "Baseline role for authenticated Studio users.")) created.Add(UserRoleId);
        if (EnsureRole(FrontDeskRoleId, StudioRoleNames.FrontDesk, "Cross-workspace reception and client-service operations.")) created.Add(FrontDeskRoleId);
        if (EnsureRole(OperationsManagerRoleId, StudioRoleNames.OperationsManager, "Overall Studio operational escalation manager.")) created.Add(OperationsManagerRoleId);
        if (EnsureRole(AdministratorRoleId, StudioRoleNames.Administrator, "Application security and unrestricted administration.")) created.Add(AdministratorRoleId);

        if (EnsureRole(FinanceManagerRoleId, StudioRoleNames.FinanceManager, "Contractual credit, final delivery authorization and outstanding receivables.")) created.Add(FinanceManagerRoleId);
        if (EnsureRole(ProductionManagerRoleId, StudioRoleNames.ProductionManager, "Production dispatch and final technical approval.")) created.Add(ProductionManagerRoleId);
        if (EnsureRole(MediaCustodianRoleId, "Media Custodian", "Authorized to soft-delete and restore Studio photographs.")) created.Add(MediaCustodianRoleId);
        SeedPermission(MediaCustodianRoleId, StudioPermissions.DeleteMedia);
        foreach (var role in new[] { FinanceManagerRoleId, ProductionManagerRoleId })
        {
            SeedPermission(role, StudioPermissions.Access);
            SeedPermission(role, StudioPermissions.ViewAllJobs);
            SeedPermission(role, StudioPermissions.ViewClientAttachments);
            SeedPermission(role, StudioPermissions.NotifyJobMembers);
        }
        SeedPermission(FinanceManagerRoleId, StudioPermissions.Billing);
        SeedPermission(ProductionManagerRoleId, StudioPermissions.AddClientAttachments);
        SeedPermission(UserRoleId, StudioPermissions.Access);
        foreach (string permission in new[]
        {
            StudioPermissions.Access,
            StudioPermissions.ViewAllJobs,
            StudioPermissions.ReplyToClients,
            StudioPermissions.NotifyJobMembers,
            StudioPermissions.Billing,
            StudioPermissions.ViewClientAttachments,
            StudioPermissions.AddClientAttachments
        })
        {
            SeedPermission(FrontDeskRoleId, permission);
        }

        foreach (string permission in new[]
        {
            StudioPermissions.Access,
            StudioPermissions.ViewAllJobs,
            StudioPermissions.NotifyJobMembers
        })
        {
            SeedPermission(OperationsManagerRoleId, permission);
        }

        SeedPermission(AdministratorRoleId, PermissionAuthorizer.AllPermissions);

        void SeedPermission(Guid role, string permission)
        { if (created.Contains(role)) EnsurePermission(role, permission); }

        foreach (string group in administratorGroups ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(group))
            {
                SaveGroupMapping(group.Trim(), AdministratorRoleId);
            }
        }
    }

    public bool HasRole(ClaimsPrincipal user, string role)
    {
        if (user.IsInRole(role)) return true;
        return GetDirectRoleKeysAsync(GetUserName(user)).GetAwaiter().GetResult()
            .Concat(GetRoleKeysForGroupsAsync(user.FindAll("directory_group").Select(c => c.Value).ToArray()).GetAwaiter().GetResult())
            .Contains(role, StringComparer.OrdinalIgnoreCase);
    }

    public bool HasPermission(ClaimsPrincipal user, string permission)
    {
        string userName = GetUserName(user);
        string permissionKey = NormalizePermission(permission);
        string[] groups = user.FindAll("directory_group").Select(claim => claim.Value).ToArray();
        PermissionAuthorizer authorizer = new(this);
        return authorizer.CanAsync(AuthorizationSubject.Create(userName, groups), permissionKey)
            .GetAwaiter()
            .GetResult();
    }

    public GlobalOperationalPermissions GetEffectivePermissions(string userName)
    {
        string[] directRoles = GetDirectRoleKeysAsync(userName).GetAwaiter().GetResult().ToArray();
        IReadOnlyCollection<RoleDefinition> roles = GetRolesAsync(directRoles).GetAwaiter().GetResult();
        HashSet<string> permissions = roles.SelectMany(role => role.Permissions).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool all = permissions.Contains(PermissionAuthorizer.AllPermissions);
        return new GlobalOperationalPermissions(
            all || permissions.Contains(StudioPermissions.ViewAllJobs),
            all || permissions.Contains(StudioPermissions.ReplyToClients),
            all || permissions.Contains(StudioPermissions.NotifyJobMembers),
            all || permissions.Contains(StudioPermissions.Billing),
            all || permissions.Contains(StudioPermissions.ViewClientAttachments),
            all || permissions.Contains(StudioPermissions.AddClientAttachments));
    }

    public IReadOnlyList<GlobalOperationalRole> GetRoles()
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id,name,description,is_active from studio_global_operational_roles order by name";
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<GlobalOperationalRole> roles = [];
        while (reader.Read())
        {
            Guid id = reader.GetGuid(0);
            string name = reader.GetString(1);
            roles.Add(new GlobalOperationalRole(
                id,
                name,
                reader.IsDBNull(2) ? null : reader.GetString(2),
                GetPermissionsForRole(id),
                reader.GetBoolean(3)));
        }
        return roles;
    }

    public GlobalOperationalRole CreateRole(GlobalOperationalRoleDefinitionRequest request)
    {
        (string name,string? description,string[] permissions)=ValidateDefinition(request);
        Guid id=Guid.NewGuid();
        using NpgsqlConnection connection=database.OpenConnection();
        using NpgsqlTransaction transaction=connection.BeginTransaction();
        using (NpgsqlCommand command=connection.CreateCommand())
        {
            command.Transaction=transaction;
            command.CommandText="insert into studio_global_operational_roles(id,name,description,is_active,created_at_utc,updated_at_utc) values($1,$2,$3,$4,now(),now())";
            command.Parameters.AddWithValue(id);command.Parameters.AddWithValue(name);command.Parameters.AddWithValue((object?)description??DBNull.Value);command.Parameters.AddWithValue(request.IsActive);
            command.ExecuteNonQuery();
        }
        ReplacePermissions(connection,transaction,id,permissions);
        transaction.Commit();
        return new GlobalOperationalRole(id,name,description,permissions,request.IsActive);
    }

    public GlobalOperationalRole UpdateRole(Guid roleId,GlobalOperationalRoleDefinitionRequest request)
    {
        (string name,string? description,string[] permissions)=ValidateDefinition(request);
        using NpgsqlConnection connection=database.OpenConnection();
        using NpgsqlTransaction transaction=connection.BeginTransaction();
        using (NpgsqlCommand command=connection.CreateCommand())
        {
            command.Transaction=transaction;
            command.CommandText="update studio_global_operational_roles set name=$2,description=$3,is_active=$4,updated_at_utc=now() where id=$1";
            command.Parameters.AddWithValue(roleId);command.Parameters.AddWithValue(name);command.Parameters.AddWithValue((object?)description??DBNull.Value);command.Parameters.AddWithValue(request.IsActive);
            if(command.ExecuteNonQuery()==0)throw new KeyNotFoundException("Access role was not found.");
        }
        ReplacePermissions(connection,transaction,roleId,permissions);
        transaction.Commit();
        return new GlobalOperationalRole(roleId,name,description,permissions,request.IsActive);
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

    public GlobalOperationalRoleMember SaveMember(Guid roleId, GlobalOperationalRoleMemberRequest request, NpgsqlConnection? transactionConnection = null, NpgsqlTransaction? transaction = null)
    {
        if (string.IsNullOrWhiteSpace(request.UserName)) throw new InvalidOperationException("User name is required.");
        if (!GetRoles().Any(role => role.Id == roleId && role.IsActive)) throw new KeyNotFoundException("Global operational role was not found.");

        string userName = request.UserName.Trim();
        string displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? userName : request.DisplayName.Trim();
        using NpgsqlConnection? ownedConnection = transactionConnection is null ? database.OpenConnection() : null;
        NpgsqlConnection connection = transactionConnection ?? ownedConnection!;
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into studio_global_operational_role_members(role_id,user_name,display_name,is_active,created_at_utc,updated_at_utc)
            values($1,$2,$3,$4,now(),now())
            on conflict(role_id,user_name) do update set display_name=excluded.display_name,is_active=excluded.is_active,updated_at_utc=now()
            returning role_id,user_name,display_name,is_active,created_at_utc,updated_at_utc
            """;
        command.Parameters.AddWithValue(roleId);
        command.Parameters.AddWithValue(userName);
        command.Parameters.AddWithValue(displayName);
        command.Parameters.AddWithValue(request.IsActive);
        using var reader=command.ExecuteReader();
        if(!reader.Read())throw new InvalidOperationException("The role membership was not persisted.");
        return new GlobalOperationalRoleMember(reader.GetGuid(0),reader.GetString(1),reader.GetString(2),reader.GetBoolean(3),reader.GetFieldValue<DateTimeOffset>(4),reader.GetFieldValue<DateTimeOffset>(5));
    }

    public void RemoveMember(Guid roleId, string userName, NpgsqlConnection? transactionConnection = null, NpgsqlTransaction? transaction = null)
    {
        using NpgsqlConnection? ownedConnection = transactionConnection is null ? database.OpenConnection() : null;
        NpgsqlConnection connection = transactionConnection ?? ownedConnection!;
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "delete from studio_global_operational_role_members where role_id=$1 and lower(user_name)=lower($2)";
        command.Parameters.AddWithValue(roleId);
        command.Parameters.AddWithValue(userName.Trim());
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<DirectoryGroupRoleMapping> GetGroupMappings()
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select g.group_name,r.id,r.name
            from studio_directory_group_roles g
            join studio_global_operational_roles r on r.id=g.role_id
            order by g.group_name,r.name
            """;
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<DirectoryGroupRoleMapping> results = [];
        while (reader.Read())
        {
            results.Add(new DirectoryGroupRoleMapping(reader.GetString(0), reader.GetGuid(1), reader.GetString(2)));
        }
        return results;
    }

    public void SaveGroupMapping(string groupName, Guid roleId, NpgsqlConnection? transactionConnection = null, NpgsqlTransaction? transaction = null)
    {
        using NpgsqlConnection? ownedConnection = transactionConnection is null ? database.OpenConnection() : null;
        NpgsqlConnection connection = transactionConnection ?? ownedConnection!;
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "insert into studio_directory_group_roles(group_name,role_id) values($1,$2) on conflict do nothing";
        command.Parameters.AddWithValue(groupName.Trim());
        command.Parameters.AddWithValue(roleId);
        command.ExecuteNonQuery();
    }

    public void RemoveGroupMapping(string groupName, Guid roleId, NpgsqlConnection? transactionConnection = null, NpgsqlTransaction? transaction = null)
    {
        using NpgsqlConnection? ownedConnection = transactionConnection is null ? database.OpenConnection() : null;
        NpgsqlConnection connection = transactionConnection ?? ownedConnection!;
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "delete from studio_directory_group_roles where lower(group_name)=lower($1) and role_id=$2";
        command.Parameters.AddWithValue(groupName.Trim());
        command.Parameters.AddWithValue(roleId);
        command.ExecuteNonQuery();
    }

    public Task<IReadOnlyCollection<string>> GetDirectRoleKeysAsync(string subjectId, CancellationToken cancellationToken = default)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select r.name
            from studio_global_operational_role_members m
            join studio_global_operational_roles r on r.id=m.role_id
            where m.is_active and r.is_active and lower(m.user_name)=lower($1)
            order by r.name
            """;
        command.Parameters.AddWithValue(subjectId.Trim());
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<string> results = [];
        while (reader.Read()) results.Add(reader.GetString(0));
        return Task.FromResult<IReadOnlyCollection<string>>(results);
    }

    public Task<IReadOnlyCollection<string>> GetRoleKeysForGroupsAsync(IReadOnlyCollection<string> groupNames, CancellationToken cancellationToken = default)
    {
        if (groupNames.Count == 0) return Task.FromResult<IReadOnlyCollection<string>>(Array.Empty<string>());
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select distinct r.name
            from studio_directory_group_roles g
            join studio_global_operational_roles r on r.id=g.role_id
            where r.is_active and lower(g.group_name)=any($1)
            order by r.name
            """;
        command.Parameters.AddWithValue(groupNames.Select(group => group.Trim().ToLowerInvariant()).ToArray());
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<string> results = [];
        while (reader.Read()) results.Add(reader.GetString(0));
        return Task.FromResult<IReadOnlyCollection<string>>(results);
    }

    public Task<IReadOnlyCollection<RoleDefinition>> GetRolesAsync(IReadOnlyCollection<string> roleKeys, CancellationToken cancellationToken = default)
    {
        if (roleKeys.Count == 0) return Task.FromResult<IReadOnlyCollection<RoleDefinition>>(Array.Empty<RoleDefinition>());
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select r.name,p.permission_key
            from studio_global_operational_roles r
            left join studio_global_operational_role_permissions p on p.role_id=r.id
            where r.is_active and lower(r.name)=any($1)
            order by r.name,p.permission_key
            """;
        command.Parameters.AddWithValue(roleKeys.Select(role => role.Trim().ToLowerInvariant()).ToArray());
        using NpgsqlDataReader reader = command.ExecuteReader();
        Dictionary<string,List<string>> permissions = new(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            string roleName = reader.GetString(0);
            if (!permissions.TryGetValue(roleName, out List<string>? values))
            {
                values = [];
                permissions[roleName] = values;
            }
            if (!reader.IsDBNull(1)) values.Add(reader.GetString(1));
        }
        RoleDefinition[] roles = permissions.Select(pair => new RoleDefinition(pair.Key, pair.Key, pair.Value)).ToArray();
        return Task.FromResult<IReadOnlyCollection<RoleDefinition>>(roles);
    }

    private bool EnsureRole(Guid id, string name, string description)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            insert into studio_global_operational_roles(id,name,description,is_active,created_at_utc,updated_at_utc)
            values($1,$2,$3,true,now(),now())
            on conflict(name) do nothing returning id
            """;
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(name);
        command.Parameters.AddWithValue(description);
        return command.ExecuteScalar() is Guid;
    }

    private void EnsurePermission(Guid roleId, string permission)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "insert into studio_global_operational_role_permissions(role_id,permission_key) values($1,$2) on conflict do nothing";
        command.Parameters.AddWithValue(roleId);
        command.Parameters.AddWithValue(permission);
        command.ExecuteNonQuery();
    }

    private static (string Name,string? Description,string[] Permissions) ValidateDefinition(GlobalOperationalRoleDefinitionRequest request)
    {
        string name=request.Name?.Trim()??string.Empty;
        if(name.Length==0)throw new InvalidOperationException("Role name is required.");
        if(name.Length>100)throw new InvalidOperationException("Role name cannot exceed 100 characters.");
        string? description=string.IsNullOrWhiteSpace(request.Description)?null:request.Description.Trim();
        if(description?.Length>500)throw new InvalidOperationException("Role description cannot exceed 500 characters.");
        string[] permissions=(request.Permissions??Array.Empty<string>()).Select(x=>x?.Trim()??string.Empty).Where(x=>x.Length>0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string[] unsupported=permissions.Where(x=>!SupportedPermissions.Contains(x)).ToArray();
        if(unsupported.Length>0)throw new InvalidOperationException("Unsupported access permission: "+string.Join(", ",unsupported));
        return (name,description,permissions);
    }

    private static void ReplacePermissions(NpgsqlConnection connection,NpgsqlTransaction transaction,Guid roleId,IReadOnlyCollection<string> permissions)
    {
        using (NpgsqlCommand clear=connection.CreateCommand())
        {clear.Transaction=transaction;clear.CommandText="delete from studio_global_operational_role_permissions where role_id=$1";clear.Parameters.AddWithValue(roleId);clear.ExecuteNonQuery();}
        foreach(string permission in permissions)
        {
            using NpgsqlCommand add=connection.CreateCommand();add.Transaction=transaction;
            add.CommandText="insert into studio_global_operational_role_permissions(role_id,permission_key) values($1,$2)";
            add.Parameters.AddWithValue(roleId);add.Parameters.AddWithValue(permission);add.ExecuteNonQuery();
        }
    }

    private IReadOnlyCollection<string> GetPermissionsForRole(Guid roleId)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select permission_key from studio_global_operational_role_permissions where role_id=$1 order by permission_key";
        command.Parameters.AddWithValue(roleId);
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<string> permissions = [];
        while (reader.Read()) permissions.Add(reader.GetString(0));
        return permissions;
    }

    private static string NormalizePermission(string permission) => permission switch
    {
        "ViewAllJobs" => StudioPermissions.ViewAllJobs,
        "ReplyToClients" => StudioPermissions.ReplyToClients,
        "NotifyJobMembers" => StudioPermissions.NotifyJobMembers,
        "Billing" => StudioPermissions.Billing,
        "ViewClientAttachments" => StudioPermissions.ViewClientAttachments,
        "AddClientAttachments" => StudioPermissions.AddClientAttachments,
        _ => permission
    };

    private static string GetUserName(ClaimsPrincipal user)
        => user.FindFirstValue("login_name") ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.Identity?.Name ?? throw new InvalidOperationException("Authenticated user has no login identifier.");
}

public static class GlobalOperationalRoleEndpoints
{
    public static void MapGlobalOperationalRoles(this WebApplication app)
    {
        GlobalOperationalRoleStore roles = new(app.Services.GetRequiredService<StudioDatabase>());
        string[] administratorGroups = app.Configuration
            .GetSection("AuthenticationPlugin:Settings:AdministrativeGroups")
            .Get<string[]>() ?? [];
        roles.EnsureSchema(administratorGroups);

        RouteGroupBuilder group = app.MapGroup("/api/security/global-roles").RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes=Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme, Roles = StudioRoleNames.Administrator });
        group.MapGet("/", () => Results.Ok(roles.GetRoles()));
        group.MapPost("/", (GlobalOperationalRoleDefinitionRequest request) => SaveDefinition(()=>roles.CreateRole(request)));
        group.MapPut("/{roleId:guid}", (Guid roleId,GlobalOperationalRoleDefinitionRequest request) => SaveDefinition(()=>roles.UpdateRole(roleId,request)));
        group.MapGet("/{roleId:guid}/members", (Guid roleId) => Results.Ok(roles.GetMembers(roleId)));
        var accounts=app.Services.GetRequiredService<RelationalAccountStore>();
        string Actor(ClaimsPrincipal principal)=>principal.FindFirstValue(AccountSecurityHosting.AccountIdClaim)
            ?? throw new UnauthorizedAccessException("A verified staff account is required.");
        group.MapPut("/{roleId:guid}/members/{userName}", (Guid roleId,string userName,GlobalOperationalRoleMemberRequest request,ClaimsPrincipal actor)=>
        {
            try
            {
                return Results.Ok(StudioStaffAuthorization.Change(accounts,actor,userName,(connection,transaction,alias)=>
                    roles.SaveMember(roleId,request with {UserName=alias},connection,transaction)));
            }
            catch(KeyNotFoundException){return Results.NotFound();}
            catch(InvalidOperationException exception){return Results.BadRequest(new {error=exception.Message});}
        });
        group.MapDelete("/{roleId:guid}/members/{userName}",(Guid roleId,string userName,ClaimsPrincipal actor)=>
        {
            StudioStaffAuthorization.Change(accounts,actor,userName,(connection,transaction,alias)=>{roles.RemoveMember(roleId,alias,connection,transaction);return true;});
            return Results.NoContent();
        });
        group.MapGet("/directory-groups",()=>Results.Ok(roles.GetGroupMappings()));
        group.MapPut("/{roleId:guid}/directory-groups/{groupName}",(Guid roleId,string groupName,ClaimsPrincipal actor)=>
        {
            string id=Actor(actor);accounts.ChangeAuthorization(id,id,(connection,transaction)=>roles.SaveGroupMapping(groupName,roleId,(NpgsqlConnection)connection,(NpgsqlTransaction)transaction));
            return Results.NoContent();
        });
        group.MapDelete("/{roleId:guid}/directory-groups/{groupName}",(Guid roleId,string groupName,ClaimsPrincipal actor)=>
        {
            string id=Actor(actor);accounts.ChangeAuthorization(id,id,(connection,transaction)=>roles.RemoveGroupMapping(groupName,roleId,(NpgsqlConnection)connection,(NpgsqlTransaction)transaction));
            return Results.NoContent();
        });

        static IResult SaveDefinition(Func<GlobalOperationalRole> save)
        {
            try{return Results.Ok(save());}
            catch(KeyNotFoundException){return Results.NotFound();}
            catch(InvalidOperationException exception){return Results.BadRequest(new {error=exception.Message});}
            catch(PostgresException exception)when(exception.SqlState==PostgresErrorCodes.UniqueViolation){return Results.Conflict(new {error="An access role with this name already exists."});}
        }

        app.MapGet("/api/security/global-role-me", (ClaimsPrincipal user) => Results.Ok(new
        {
            roles = roles.GetDirectRoleKeysAsync(GetUserName(user)).GetAwaiter().GetResult(),
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

public sealed record GlobalOperationalRole(Guid Id,string Name,string? Description,IReadOnlyCollection<string> Permissions,bool IsActive);
public sealed record GlobalOperationalRoleDefinitionRequest(string Name,string? Description,IReadOnlyCollection<string>? Permissions,bool IsActive=true);
public sealed record GlobalOperationalRoleMember(Guid RoleId,string UserName,string DisplayName,bool IsActive,DateTimeOffset CreatedAtUtc,DateTimeOffset UpdatedAtUtc);
public sealed record GlobalOperationalRoleMemberRequest(string UserName,string? DisplayName,bool IsActive = true);
public sealed record DirectoryGroupRoleMapping(string GroupName,Guid RoleId,string RoleName);

