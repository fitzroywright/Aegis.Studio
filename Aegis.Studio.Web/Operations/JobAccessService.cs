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

    public JobPermissionSet GetPermissions(ClaimsPrincipal user,Guid jobId)
        =>GetPermissionMap(user,jobId).GetValueOrDefault(jobId,JobPermissionSet.None);
    public IReadOnlyList<Guid> GetAccessibleJobIds(ClaimsPrincipal user,bool requireAttachments=false)
        =>GetPermissionMap(user).Where(x=>x.Value.AccessJob&&(!requireAttachments||x.Value.ViewAttachments)).Select(x=>x.Key).ToArray();

    // Production is readable to every signed-in Studio user; write endpoints enforce ManageProduction separately.
    public IReadOnlyList<Guid> GetProductionJobIds(ClaimsPrincipal user)
    {
        if(user.Identity?.IsAuthenticated!=true)return Array.Empty<Guid>();
        using var c=database.OpenConnection();using var q=new NpgsqlCommand("select id from studio_jobs",c);
        using var r=q.ExecuteReader();var ids=new List<Guid>();while(r.Read())ids.Add(r.GetGuid(0));return ids;
    }

    // The same permission union as GetPermissions, fetched once for list screens.
    public IReadOnlyDictionary<Guid,JobPermissionSet> GetPermissionMap(ClaimsPrincipal user,Guid? jobId=null)
    {
        var result=new Dictionary<Guid,JobPermissionSet>();
        bool administrator=user.IsInRole("Administrator");
        JobPermissionSet global=administrator?JobPermissionSet.Full:FromGlobalOperationalRole(user);
        using var c=database.OpenConnection();
        using var cmd=new NpgsqlCommand("""
            select j.id,array[coalesce(w.access_workspace,false),coalesce(w.can_view,false),coalesce(w.can_view,false),coalesce(w.reply_requester,false),coalesce(w.view_attachments,false),coalesce(w.add_attachments,false),coalesce(w.edit_job,false),coalesce(w.edit_job,false),coalesce(w.edit_job,false),coalesce(w.manage_schedule,false),coalesce(w.manage_production,false),coalesce(w.edit_job,false),coalesce(w.edit_job,false),coalesce(w.edit_job,false),coalesce(w.can_view,false),coalesce(w.edit_job,false),coalesce(w.notify_members,false),coalesce(w.manage_members,false),coalesce(w.admin_workspace,false)],array[coalesce(m.access_job,false),coalesce(m.view_job,false),coalesce(m.view_conversation,false),coalesce(m.reply_requester,false),coalesce(m.view_attachments,false),coalesce(m.add_attachments,false),coalesce(m.edit_job,false),coalesce(m.change_status,false),coalesce(m.assign_staff,false),coalesce(m.manage_schedule,false),coalesce(m.manage_production,false),coalesce(m.view_financials,false),coalesce(m.record_financial_activity,false),coalesce(m.use_pos,false),coalesce(m.view_pos_transactions,false),coalesce(m.refund_void_pos,false),coalesce(m.notify_job_members,false),coalesce(m.manage_job_members,false),coalesce(m.admin_job,false)],
              m.job_id is not null,coalesce(m.inherit_workspace_permissions,true),coalesce(w.admin_workspace,false),lower(coalesce(j.job_lead,''))=lower($1),
              exists(select 1 from studio_workflow_crew wc join studio_workflow_sessions ws on ws.id=wc.session_id where ws.job_id=j.id and lower(wc.user_name)=lower($1) and ws.required),
              exists(select 1 from studio_job_activities a where a.job_id=j.id and lower(a.assigned_to)=lower($1) and a.status<>'Cancelled'),
              exists(select 1 from studio_job_owner_assignments a where a.id=j.owner_assignment_id and lower(a.assigned_to)=lower($1) and a.status='Declined' and j.job_lead is null), coalesce(m.delete_media,false)
            from studio_jobs j
            left join lateral(select bool_or(access_workspace) as access_workspace,bool_or(can_view) as can_view,bool_or(reply_requester) as reply_requester,bool_or(view_attachments) as view_attachments,bool_or(add_attachments) as add_attachments,bool_or(edit_job) as edit_job,bool_or(manage_schedule) as manage_schedule,bool_or(manage_production) as manage_production,bool_or(notify_members) as notify_members,bool_or(manage_members) as manage_members,bool_or(admin_workspace) as admin_workspace from studio_workspace_members wm
              where lower(wm.user_name)=lower($1) and wm.is_active and (wm.workspace_id=j.workspace_id or exists(
                select 1 from studio_job_workspace_links l where l.job_id=j.id and l.workspace_id=wm.workspace_id))) w on true
            left join studio_job_members m on m.job_id=j.id and lower(m.user_name)=lower($1) and m.is_active
            where ($2::uuid is null or j.id=$2)
            """,c);cmd.Parameters.AddWithValue(GetUserName(user));cmd.Parameters.AddWithValue((object?)jobId??DBNull.Value);
        using var reader=cmd.ExecuteReader();
        while(reader.Read())
        {
            JobPermissionSet inherited=reader.GetBoolean(5)?JobPermissionSet.Full:FromArray(reader.GetFieldValue<bool[]>(1));
            JobPermissionSet direct=FromArray(reader.GetFieldValue<bool[]>(2)) with { DeleteMedia = reader.GetBoolean(10) };
            if(direct.AdminJob)direct=JobPermissionSet.Full;
            var permissions=administrator?JobPermissionSet.Full:reader.GetBoolean(3)
                ?Union(reader.GetBoolean(4)?Union(global,inherited):global,direct):Union(global,inherited);
            if(reader.GetBoolean(6))permissions=Union(permissions,JobPermissionSet.WorkflowOwner);
            else if(reader.GetBoolean(7)||reader.GetBoolean(8))permissions=Union(permissions,JobPermissionSet.WorkflowContributor);
            if(reader.GetBoolean(9))permissions=Union(permissions,JobPermissionSet.Viewer);
            if(permissions.AccessJob||permissions.ViewJob||permissions.ManageProduction||permissions.DeleteMedia)result[reader.GetGuid(0)]=permissions;
        }
        return result;
    }
    private static JobPermissionSet FromArray(bool[] values)=>new(values[0],values[1],values[2],values[3],values[4],values[5],values[6],values[7],values[8],values[9],values[10],values[11],values[12],values[13],values[14],values[15],values[16],values[17],values[18]);

    public IReadOnlyList<JobMember> GetMembers(Guid jobId)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select job_id,user_name,display_name,is_active,inherit_workspace_permissions,
                   access_job,view_job,view_conversation,reply_requester,view_attachments,add_attachments,
                   edit_job,change_status,assign_staff,manage_schedule,manage_production,view_financials,
                   record_financial_activity,use_pos,view_pos_transactions,refund_void_pos,notify_job_members,
                   manage_job_members,admin_job,created_at_utc,updated_at_utc,delete_media
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
             notify_job_members,manage_job_members,admin_job,delete_media,created_at_utc,updated_at_utc)
            values ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21,$22,$23,$24,$25,now(),now())
            on conflict(job_id,user_name) do update set
              display_name=excluded.display_name,is_active=excluded.is_active,inherit_workspace_permissions=excluded.inherit_workspace_permissions,
              access_job=excluded.access_job,view_job=excluded.view_job,view_conversation=excluded.view_conversation,
              reply_requester=excluded.reply_requester,view_attachments=excluded.view_attachments,add_attachments=excluded.add_attachments,
              edit_job=excluded.edit_job,change_status=excluded.change_status,assign_staff=excluded.assign_staff,
              manage_schedule=excluded.manage_schedule,manage_production=excluded.manage_production,view_financials=excluded.view_financials,
              record_financial_activity=excluded.record_financial_activity,use_pos=excluded.use_pos,
              view_pos_transactions=excluded.view_pos_transactions,refund_void_pos=excluded.refund_void_pos,
              notify_job_members=excluded.notify_job_members,manage_job_members=excluded.manage_job_members,
              admin_job=excluded.admin_job,delete_media=excluded.delete_media,updated_at_utc=now()
            """;
        object[] values = [jobId,userName,displayName,request.IsActive,request.InheritWorkspacePermissions,
            permissions.AccessJob,permissions.ViewJob,permissions.ViewConversation,permissions.ReplyRequester,
            permissions.ViewAttachments,permissions.AddAttachments,permissions.EditJob,permissions.ChangeStatus,
            permissions.AssignStaff,permissions.ManageSchedule,permissions.ManageProduction,permissions.ViewFinancials,
            permissions.RecordFinancialActivity,permissions.UsePos,permissions.ViewPosTransactions,permissions.RefundVoidPos,
            permissions.NotifyJobMembers,permissions.ManageJobMembers,permissions.AdminJob,permissions.DeleteMedia];
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
        // Legacy callers must not create permanent grants for temporary assignments.
        // Owner, crew and task-assignee capabilities are derived by GetPermissions.
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
        bool productionManager = globalRoles.HasRole(user, StudioRoleNames.ProductionManager);

        if (!viewAllJobs && !reply && !notify && !billing && !viewAttachments && !addAttachments && !productionManager)
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
            productionManager,
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
            a.AdminJob || b.AdminJob) { DeleteMedia = a.DeleteMedia || b.DeleteMedia };

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
        "DeleteMedia" => p.DeleteMedia,
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
            new JobPermissionSet(r.GetBoolean(5),r.GetBoolean(6),r.GetBoolean(7),r.GetBoolean(8),r.GetBoolean(9),r.GetBoolean(10),r.GetBoolean(11),r.GetBoolean(12),r.GetBoolean(13),r.GetBoolean(14),r.GetBoolean(15),r.GetBoolean(16),r.GetBoolean(17),r.GetBoolean(18),r.GetBoolean(19),r.GetBoolean(20),r.GetBoolean(21),r.GetBoolean(22),r.GetBoolean(23)) { DeleteMedia = r.GetBoolean(26) },
            r.GetFieldValue<DateTimeOffset>(24),r.GetFieldValue<DateTimeOffset>(25));
}

public sealed record JobMember(Guid JobId,string UserName,string DisplayName,bool IsActive,bool InheritWorkspacePermissions,JobPermissionSet Permissions,DateTimeOffset CreatedAtUtc,DateTimeOffset UpdatedAtUtc);
public sealed record JobMemberRequest(string UserName,string? DisplayName,bool IsActive,bool InheritWorkspacePermissions,JobPermissionSet? Permissions);
public sealed record JobPermissionSet(bool AccessJob,bool ViewJob,bool ViewConversation,bool ReplyRequester,bool ViewAttachments,bool AddAttachments,bool EditJob,bool ChangeStatus,bool AssignStaff,bool ManageSchedule,bool ManageProduction,bool ViewFinancials,bool RecordFinancialActivity,bool UsePos,bool ViewPosTransactions,bool RefundVoidPos,bool NotifyJobMembers,bool ManageJobMembers,bool AdminJob)
{
    public bool DeleteMedia { get; init; }

    public static JobPermissionSet None => new(false,false,false,false,false,false,false,false,false,false,false,false,false,false,false,false,false,false,false);
    public static JobPermissionSet Viewer => new(true,true,true,false,true,false,false,false,false,false,false,false,false,false,false,false,false,false,false);
    public static JobPermissionSet Contributor => new(true,true,true,false,true,true,false,false,false,false,true,false,false,false,false,false,true,false,false);
    public static JobPermissionSet JobStaff => new(true,true,true,true,true,true,false,false,false,true,true,false,false,false,false,false,true,false,false);
    public static JobPermissionSet JobManager => new(true,true,true,true,true,true,true,true,true,true,true,true,true,true,true,false,true,true,false);
    public static JobPermissionSet WorkflowOwner => new(true,true,true,true,true,true,true,true,true,true,true,false,false,false,false,false,true,false,false);
    public static JobPermissionSet WorkflowContributor => new(true,true,true,false,true,true,false,false,false,false,true,false,false,false,false,false,false,false,false);
    public static JobPermissionSet Full => new(true,true,true,true,true,true,true,true,true,true,true,true,true,true,true,true,true,true,true) { DeleteMedia = true };
}

