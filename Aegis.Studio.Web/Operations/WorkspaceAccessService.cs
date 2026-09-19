using System.Security.Claims;
using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Operations;

public sealed class WorkspaceAccessService
{
    private readonly StudioDatabase database;

    public WorkspaceAccessService(StudioDatabase database)
    {
        this.database = database;
    }

    public static readonly string[] PermissionNames =
    [
        "AccessWorkspace",
        "View",
        "ReplyRequester",
        "ViewAttachments",
        "AddAttachments",
        "CreateJob",
        "EditJob",
        "ManageSchedule",
        "ManageProduction",
        "NotifyMembers",
        "EditWorkspace",
        "ManageMembers",
        "AdminWorkspace",
        "SendAsSelf"
    ];

    public bool HasPermission(ClaimsPrincipal user, Guid workspaceId, string permission)
    {
        if (user.IsInRole("Administrator"))
        {
            return true;
        }

        string userName = GetUserName(user);
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = $"select {Column(permission)} from studio_workspace_members where workspace_id=$1 and lower(user_name)=lower($2) and is_active";
        command.Parameters.AddWithValue(workspaceId);
        command.Parameters.AddWithValue(userName);
        object? value = command.ExecuteScalar();
        return value is bool allowed && allowed;
    }

    public IReadOnlyList<Guid> GetAccessibleWorkspaceIds(ClaimsPrincipal user)
    {
        if (user.IsInRole("Administrator"))
        {
            using NpgsqlConnection adminConnection = database.OpenConnection();
            using NpgsqlCommand adminCommand = adminConnection.CreateCommand();
            adminCommand.CommandText = "select id from studio_workspaces where is_enabled order by sort_order,name";
            using NpgsqlDataReader adminReader = adminCommand.ExecuteReader();
            List<Guid> adminValues = [];
            while (adminReader.Read()) adminValues.Add(adminReader.GetGuid(0));
            return adminValues;
        }

        string userName = GetUserName(user);
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select workspace_id from studio_workspace_members where lower(user_name)=lower($1) and is_active and access_workspace order by workspace_id";
        command.Parameters.AddWithValue(userName);
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<Guid> values = [];
        while (reader.Read()) values.Add(reader.GetGuid(0));
        return values;
    }

    public IReadOnlyList<WorkspaceMember> GetMembers(Guid workspaceId)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select workspace_id,user_name,display_name,is_active,access_workspace,can_view,reply_requester,view_attachments,add_attachments,create_job,edit_job,manage_schedule,manage_production,notify_members,edit_workspace,manage_members,admin_workspace,send_as_self,created_at_utc,updated_at_utc
            from studio_workspace_members where workspace_id=$1 order by display_name,user_name
            """;
        command.Parameters.AddWithValue(workspaceId);
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<WorkspaceMember> values = [];
        while (reader.Read()) values.Add(Read(reader));
        return values;
    }

    public WorkspaceMember SaveMember(Guid workspaceId, WorkspaceMemberRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.UserName)) throw new InvalidOperationException("User name is required.");
        string userName = request.UserName.Trim();
        string displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? userName : request.DisplayName.Trim();
        WorkspacePermissionSet p = request.Permissions ?? WorkspacePermissionSet.ViewOnly;
        if (p.AdminWorkspace)
        {
            p = WorkspacePermissionSet.Full;
        }

        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            insert into studio_workspace_members
            (workspace_id,user_name,display_name,is_active,access_workspace,can_view,reply_requester,view_attachments,add_attachments,create_job,edit_job,manage_schedule,manage_production,notify_members,edit_workspace,manage_members,admin_workspace,send_as_self,created_at_utc,updated_at_utc)
            values ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,now(),now())
            on conflict(workspace_id,user_name) do update set
              display_name=excluded.display_name,is_active=excluded.is_active,access_workspace=excluded.access_workspace,can_view=excluded.can_view,
              reply_requester=excluded.reply_requester,view_attachments=excluded.view_attachments,add_attachments=excluded.add_attachments,
              create_job=excluded.create_job,edit_job=excluded.edit_job,manage_schedule=excluded.manage_schedule,manage_production=excluded.manage_production,
              notify_members=excluded.notify_members,edit_workspace=excluded.edit_workspace,manage_members=excluded.manage_members,admin_workspace=excluded.admin_workspace,send_as_self=excluded.send_as_self,updated_at_utc=now()
            """;
        object[] values = [workspaceId,userName,displayName,request.IsActive,p.AccessWorkspace,p.View,p.ReplyRequester,p.ViewAttachments,p.AddAttachments,p.CreateJob,p.EditJob,p.ManageSchedule,p.ManageProduction,p.NotifyMembers,p.EditWorkspace,p.ManageMembers,p.AdminWorkspace,p.SendAsSelf];
        foreach (object value in values) command.Parameters.AddWithValue(value);
        command.ExecuteNonQuery();
        return GetMembers(workspaceId).First(x => string.Equals(x.UserName,userName,StringComparison.OrdinalIgnoreCase));
    }

    public void RemoveMember(Guid workspaceId, string userName)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "delete from studio_workspace_members where workspace_id=$1 and lower(user_name)=lower($2)";
        command.Parameters.AddWithValue(workspaceId);
        command.Parameters.AddWithValue(userName.Trim());
        command.ExecuteNonQuery();
    }

    public WorkspacePermissionSet GetPermissions(ClaimsPrincipal user, Guid workspaceId)
    {
        if (user.IsInRole("Administrator")) return WorkspacePermissionSet.Full;
        string userName = GetUserName(user);
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select access_workspace,can_view,reply_requester,view_attachments,add_attachments,create_job,edit_job,manage_schedule,manage_production,notify_members,edit_workspace,manage_members,admin_workspace,send_as_self from studio_workspace_members where workspace_id=$1 and lower(user_name)=lower($2) and is_active";
        command.Parameters.AddWithValue(workspaceId); command.Parameters.AddWithValue(userName);
        using NpgsqlDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadPermissions(reader) : WorkspacePermissionSet.None;
    }

    private static string Column(string permission) => permission switch
    {
        "AccessWorkspace" => "access_workspace", "View" => "can_view", "ReplyRequester" => "reply_requester", "ViewAttachments" => "view_attachments", "AddAttachments" => "add_attachments", "CreateJob" => "create_job", "EditJob" => "edit_job", "ManageSchedule" => "manage_schedule", "ManageProduction" => "manage_production", "NotifyMembers" => "notify_members", "EditWorkspace" => "edit_workspace", "ManageMembers" => "manage_members", "AdminWorkspace" => "admin_workspace", "SendAsSelf" => "send_as_self", _ => throw new ArgumentOutOfRangeException(nameof(permission))
    };
    private static string GetUserName(ClaimsPrincipal user) => user.FindFirstValue("login_name") ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.Identity?.Name ?? throw new InvalidOperationException("Authenticated user has no login identifier.");
    private static WorkspacePermissionSet ReadPermissions(NpgsqlDataReader r) => new(r.GetBoolean(0),r.GetBoolean(1),r.GetBoolean(2),r.GetBoolean(3),r.GetBoolean(4),r.GetBoolean(5),r.GetBoolean(6),r.GetBoolean(7),r.GetBoolean(8),r.GetBoolean(9),r.GetBoolean(10),r.GetBoolean(11),r.GetBoolean(12),r.GetBoolean(13));
    private static WorkspaceMember Read(NpgsqlDataReader r) => new(r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetBoolean(3),new WorkspacePermissionSet(r.GetBoolean(4),r.GetBoolean(5),r.GetBoolean(6),r.GetBoolean(7),r.GetBoolean(8),r.GetBoolean(9),r.GetBoolean(10),r.GetBoolean(11),r.GetBoolean(12),r.GetBoolean(13),r.GetBoolean(14),r.GetBoolean(15),r.GetBoolean(16),r.GetBoolean(17)),r.GetFieldValue<DateTimeOffset>(18),r.GetFieldValue<DateTimeOffset>(19));
}

public sealed record WorkspaceMember(Guid WorkspaceId,string UserName,string DisplayName,bool IsActive,WorkspacePermissionSet Permissions,DateTimeOffset CreatedAtUtc,DateTimeOffset UpdatedAtUtc);
public sealed record WorkspaceMemberRequest(string UserName,string? DisplayName,bool IsActive,WorkspacePermissionSet? Permissions);
public sealed record WorkspacePermissionSet(bool AccessWorkspace,bool View,bool ReplyRequester,bool ViewAttachments,bool AddAttachments,bool CreateJob,bool EditJob,bool ManageSchedule,bool ManageProduction,bool NotifyMembers,bool EditWorkspace,bool ManageMembers,bool AdminWorkspace,bool SendAsSelf)
{
    public static WorkspacePermissionSet None => new(false,false,false,false,false,false,false,false,false,false,false,false,false,false);
    public static WorkspacePermissionSet ViewOnly => new(true,true,false,true,false,false,false,false,false,false,false,false,false,false);
    public static WorkspacePermissionSet Full => new(true,true,true,true,true,true,true,true,true,true,true,true,true,true);
}
