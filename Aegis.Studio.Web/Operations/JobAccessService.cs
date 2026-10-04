using System.Security.Claims;
using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Operations;

public sealed class JobAccessService
{
    private readonly StudioDatabase database;
    private readonly WorkspaceAccessService workspaceAccess;
    private readonly GlobalOperationalRoleStore globalRoles;

    public JobAccessService(StudioDatabase database)
    {
        this.database = database;
        workspaceAccess = new WorkspaceAccessService(database);
        globalRoles = new GlobalOperationalRoleStore(database);
    }

    public bool HasPermission(ClaimsPrincipal user, Guid jobId, string permission)
    {
        JobPermissionSet permissions = GetPermissions(user, jobId);
        return GetPermission(permissions, permission);
    }

    public JobPermissionSet GetPermissions(ClaimsPrincipal user, Guid jobId)
    {
        if (user.IsInRole("Administrator"))
        {
            return JobPermissionSet.Full;
        }

        JobPermissionSet global = FromGlobalOperationalRole(user);
        Guid? workspaceId = GetWorkspaceId(jobId);
        JobPermissionSet inherited = workspaceId.HasValue
            ? FromWorkspace(workspaceAccess.GetPermissions(user, workspaceId.Value))
            : JobPermissionSet.None;
        inherited = Union(inherited, global);

        string userName = GetUserName(user);
        JobMember? member = GetMembers(jobId).FirstOrDefault(x => x.IsActive && string.Equals(x.UserName, userName, StringComparison.OrdinalIgnoreCase));
        if (member is null)
        {
            return inherited;
        }

        if (member.Permissions.AdminJob)
        {
            return JobPermissionSet.Full;
        }

        return member.InheritWorkspacePermissions ? Union(inherited, member.Permissions) : Union(global, member.Permissions);
    }

    public IReadOnlyList<Guid> GetAccessibleJobIds(ClaimsPrincipal user, bool requireAttachments = false)
    {
        bool administrator = user.IsInRole("Administrator");
        bool globalView = administrator || globalRoles.HasPermission(user, "ViewAllJobs");
        bool globalAttachments = administrator || (requireAttachments && globalRoles.HasPermission(user, "ViewClientAttachments"));
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        // Match GetPermissions' inheritance and direct-job overrides in one read.
        // Global ViewAllJobs still grants the same list access as the previous implementation.
        command.CommandText = """
            select j.id from studio_jobs j
            left join studio_workspace_members w on w.workspace_id=j.workspace_id and lower(w.user_name)=lower($1) and w.is_active
            left join studio_job_members m on m.job_id=j.id and lower(m.user_name)=lower($1) and m.is_active
            where ($2 or coalesce(m.admin_job,false) or coalesce(m.access_job,false)
                or ((m.job_id is null or m.inherit_workspace_permissions) and coalesce(w.access_workspace,false)))
              and (not $3 or $4 or coalesce(m.admin_job,false) or coalesce(m.view_attachments,false)
                or ((m.job_id is null or m.inherit_workspace_permissions) and coalesce(w.view_attachments,false)))
            order by j.created_at_utc desc
            """;
        command.Parameters.AddWithValue(GetUserName(user));
        command.Parameters.AddWithValue(globalView);
        command.Parameters.AddWithValue(requireAttachments);
        command.Parameters.AddWithValue(globalAttachments);
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<Guid> result = [];
        while (reader.Read()) result.Add(reader.GetGuid(0));
        return result;
    }

    public IReadOnlyList<JobMember> GetMembers(Guid jobId)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select job_id,user_name,display_name,is_active,inherit_workspace_permissions,
                   access_job,view_job,view_conversation,reply_requester,view_attachments,add_attachments,
                   edit_job,change_status,assign_staff,manage_schedule,manage_production,view_financials,
                   record_financial_activity,use_pos,view_pos_transactions,refund_void_pos,notify_job_members,
                   manage_job_members,admin_job,created_at_utc,updated_at_utc
            from studio_job_members where job_id=$1 order by display_name,user_name
            """;
        command.Parameters.AddWithValue(jobId);
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<JobMember> values = [];
        while (reader.Read()) values.Add(ReadMember(reader));
        return values;
    }

    public JobMember SaveMember(Guid jobId, JobMemberRequest request)
    {
        if (GetWorkspaceId(jobId) is null) throw new KeyNotFoundException("Job was not found.");
        if (string.IsNullOrWhiteSpace(request.UserName)) throw new InvalidOperationException("User name is required.");

        string userName = request.UserName.Trim();
        string displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? userName : request.DisplayName.Trim();
        JobPermissionSet permissions = request.Permissions ?? JobPermissionSet.Viewer;
        if (permissions.AdminJob) permissions = JobPermissionSet.Full;

        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            insert into studio_job_members
            (job_id,user_name,display_name,is_active,inherit_workspace_permissions,access_job,view_job,view_conversation,
             reply_requester,view_attachments,add_attachments,edit_job,change_status,assign_staff,manage_schedule,
             manage_production,view_financials,record_financial_activity,use_pos,view_pos_transactions,refund_void_pos,
             notify_job_members,manage_job_members,admin_job,created_at_utc,updated_at_utc)
            values ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21,$22,$23,$24,now(),now())
            on conflict(job_id,user_name) do update set
              display_name=excluded.display_name,is_active=excluded.is_active,inherit_workspace_permissions=excluded.inherit_workspace_permissions,
              access_job=excluded.access_job,view_job=excluded.view_job,view_conversation=excluded.view_conversation,
              reply_requester=excluded.reply_requester,view_attachments=excluded.view_attachments,add_attachments=excluded.add_attachments,
              edit_job=excluded.edit_job,change_status=excluded.change_status,assign_staff=excluded.assign_staff,
              manage_schedule=excluded.manage_schedule,manage_production=excluded.manage_production,view_financials=excluded.view_financials,
              record_financial_activity=excluded.record_financial_activity,use_pos=excluded.use_pos,
              view_pos_transactions=excluded.view_pos_transactions,refund_void_pos=excluded.refund_void_pos,
              notify_job_members=excluded.notify_job_members,manage_job_members=excluded.manage_job_members,
              admin_job=excluded.admin_job,updated_at_utc=now()
            """;
        object[] values = [jobId,userName,displayName,request.IsActive,request.InheritWorkspacePermissions,
            permissions.AccessJob,permissions.ViewJob,permissions.ViewConversation,permissions.ReplyRequester,
            permissions.ViewAttachments,permissions.AddAttachments,permissions.EditJob,permissions.ChangeStatus,
            permissions.AssignStaff,permissions.ManageSchedule,permissions.ManageProduction,permissions.ViewFinancials,
            permissions.RecordFinancialActivity,permissions.UsePos,permissions.ViewPosTransactions,permissions.RefundVoidPos,
            permissions.NotifyJobMembers,permissions.ManageJobMembers,permissions.AdminJob];
        foreach (object value in values) command.Parameters.AddWithValue(value);
        command.ExecuteNonQuery();
        return GetMembers(jobId).First(x => string.Equals(x.UserName, userName, StringComparison.OrdinalIgnoreCase));
    }

    public void RemoveMember(Guid jobId, string userName)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "delete from studio_job_members where job_id=$1 and lower(user_name)=lower($2)";
        command.Parameters.AddWithValue(jobId);
        command.Parameters.AddWithValue(userName.Trim());
        command.ExecuteNonQuery();
    }

    public void EnsureAssignedMember(Guid jobId, string userName)
    {
        if (string.IsNullOrWhiteSpace(userName)) return;
        JobMember? existing = GetMembers(jobId).FirstOrDefault(x => string.Equals(x.UserName, userName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return;
        SaveMember(jobId, new JobMemberRequest(userName, userName, true, true, JobPermissionSet.JobStaff));
    }

    public Guid? GetWorkspaceId(Guid jobId)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select workspace_id from studio_jobs where id=$1";
        command.Parameters.AddWithValue(jobId);
        return command.ExecuteScalar() is Guid id ? id : null;
    }

    private JobPermissionSet FromGlobalOperationalRole(ClaimsPrincipal user)
    {
        bool viewAllJobs = globalRoles.HasPermission(user, "ViewAllJobs");
        bool reply = globalRoles.HasPermission(user, "ReplyToClients");
        bool notify = globalRoles.HasPermission(user, "NotifyJobMembers");
        bool billing = globalRoles.HasPermission(user, "Billing");
        bool viewAttachments = globalRoles.HasPermission(user, "ViewClientAttachments");
        bool addAttachments = globalRoles.HasPermission(user, "AddClientAttachments");

        if (!viewAllJobs && !reply && !notify && !billing && !viewAttachments && !addAttachments)
        {
            return JobPermissionSet.None;
        }

        return new JobPermissionSet(
            viewAllJobs,
            viewAllJobs,
            viewAllJobs || reply,
            reply,
            viewAttachments,
            addAttachments,
            false,
            false,
            false,
            false,
            false,
            billing,
            billing,
            billing,
            billing,
            false,
            notify,
            false,
            false);
    }

    private static JobPermissionSet FromWorkspace(WorkspacePermissionSet p)
        => new(
            p.AccessWorkspace,
            p.View,
            p.View,
            p.ReplyRequester,
            p.ViewAttachments,
            p.AddAttachments,
            p.EditJob,
            p.EditJob,
            p.EditJob,
            p.ManageSchedule,
            p.ManageProduction,
            p.EditJob,
            p.EditJob,
            p.EditJob,
            p.View,
            p.EditJob,
            p.NotifyMembers,
            p.ManageMembers,
            p.AdminWorkspace);

    private static JobPermissionSet Union(JobPermissionSet a, JobPermissionSet b)
        => new(
            a.AccessJob || b.AccessJob,
            a.ViewJob || b.ViewJob,
            a.ViewConversation || b.ViewConversation,
            a.ReplyRequester || b.ReplyRequester,
            a.ViewAttachments || b.ViewAttachments,
            a.AddAttachments || b.AddAttachments,
            a.EditJob || b.EditJob,
            a.ChangeStatus || b.ChangeStatus,
            a.AssignStaff || b.AssignStaff,
            a.ManageSchedule || b.ManageSchedule,
            a.ManageProduction || b.ManageProduction,
            a.ViewFinancials || b.ViewFinancials,
            a.RecordFinancialActivity || b.RecordFinancialActivity,
            a.UsePos || b.UsePos,
            a.ViewPosTransactions || b.ViewPosTransactions,
            a.RefundVoidPos || b.RefundVoidPos,
            a.NotifyJobMembers || b.NotifyJobMembers,
            a.ManageJobMembers || b.ManageJobMembers,
            a.AdminJob || b.AdminJob);

    private static bool GetPermission(JobPermissionSet p, string permission) => permission switch
    {
        "AccessJob" => p.AccessJob,
        "ViewJob" => p.ViewJob,
        "ViewConversation" => p.ViewConversation,
        "ReplyRequester" => p.ReplyRequester,
        "ViewAttachments" => p.ViewAttachments,
        "AddAttachments" => p.AddAttachments,
        "EditJob" => p.EditJob,
        "ChangeStatus" => p.ChangeStatus,
        "AssignStaff" => p.AssignStaff,
        "ManageSchedule" => p.ManageSchedule,
        "ManageProduction" => p.ManageProduction,
        "ViewFinancials" => p.ViewFinancials,
        "RecordFinancialActivity" => p.RecordFinancialActivity,
        "UsePos" => p.UsePos,
        "ViewPosTransactions" => p.ViewPosTransactions,
        "RefundVoidPos" => p.RefundVoidPos,
        "NotifyJobMembers" => p.NotifyJobMembers,
        "ManageJobMembers" => p.ManageJobMembers,
        "AdminJob" => p.AdminJob,
        _ => false
    };

    private static string GetUserName(ClaimsPrincipal user)
        => user.FindFirstValue("login_name") ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.Identity?.Name ?? throw new InvalidOperationException("Authenticated user has no login identifier.");

    private static JobMember ReadMember(NpgsqlDataReader r)
        => new(
            r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetBoolean(3),r.GetBoolean(4),
            new JobPermissionSet(r.GetBoolean(5),r.GetBoolean(6),r.GetBoolean(7),r.GetBoolean(8),r.GetBoolean(9),r.GetBoolean(10),r.GetBoolean(11),r.GetBoolean(12),r.GetBoolean(13),r.GetBoolean(14),r.GetBoolean(15),r.GetBoolean(16),r.GetBoolean(17),r.GetBoolean(18),r.GetBoolean(19),r.GetBoolean(20),r.GetBoolean(21),r.GetBoolean(22),r.GetBoolean(23)),
            r.GetFieldValue<DateTimeOffset>(24),r.GetFieldValue<DateTimeOffset>(25));
}

public sealed record JobMember(Guid JobId,string UserName,string DisplayName,bool IsActive,bool InheritWorkspacePermissions,JobPermissionSet Permissions,DateTimeOffset CreatedAtUtc,DateTimeOffset UpdatedAtUtc);
public sealed record JobMemberRequest(string UserName,string? DisplayName,bool IsActive,bool InheritWorkspacePermissions,JobPermissionSet? Permissions);
public sealed record JobPermissionSet(bool AccessJob,bool ViewJob,bool ViewConversation,bool ReplyRequester,bool ViewAttachments,bool AddAttachments,bool EditJob,bool ChangeStatus,bool AssignStaff,bool ManageSchedule,bool ManageProduction,bool ViewFinancials,bool RecordFinancialActivity,bool UsePos,bool ViewPosTransactions,bool RefundVoidPos,bool NotifyJobMembers,bool ManageJobMembers,bool AdminJob)
{
    public static JobPermissionSet None => new(false,false,false,false,false,false,false,false,false,false,false,false,false,false,false,false,false,false,false);
    public static JobPermissionSet Viewer => new(true,true,true,false,true,false,false,false,false,false,false,false,false,false,false,false,false,false,false);
    public static JobPermissionSet Contributor => new(true,true,true,false,true,true,false,false,false,false,true,false,false,false,false,false,true,false,false);
    public static JobPermissionSet JobStaff => new(true,true,true,true,true,true,false,false,false,true,true,false,false,false,false,false,true,false,false);
    public static JobPermissionSet JobManager => new(true,true,true,true,true,true,true,true,true,true,true,true,true,true,true,false,true,true,false);
    public static JobPermissionSet Full => new(true,true,true,true,true,true,true,true,true,true,true,true,true,true,true,true,true,true,true);
}

