using System.Text.Json;
using Npgsql;
namespace Aegis.Studio.Web.Crm;

public sealed partial class CrmStore
{
    public JsonElement Tasks(Guid? client=null,bool overdue=false)
    {
        access.Demand(CrmPermissions.View);if(client.HasValue)access.Client(client.Value);
        return Query("""
            select coalesce(jsonb_agg(item order by due),'[]'::jsonb)::text from (select jsonb_build_object('id',t.id,'clientId',t.client_id,'displayName',c.display_name,
            'workspaceId',t.workspace_id,'jobId',t.job_id,'enquiryId',t.enquiry_id,'assignedTo',t.assigned_to,'taskType',t.task_type,'priority',t.priority,'status',t.status,
            'dueAtUtc',t.due_at_utc,'notes',t.notes,'completedAtUtc',t.completed_at_utc) item,t.due_at_utc due from studio_crm_tasks t join studio_clients c on c.id=t.client_id
            where ($1::uuid is null or t.client_id=$1) and
            """+CrmAccess.Scope+"""
            and ($2 or t.workspace_id=any($3)) and ($4=false or (t.status in ('Open','In Progress') and t.due_at_utc<now()))
            and (t.job_id is null or t.job_id=any($5)) order by t.due_at_utc,t.id limit 500) tasks
            """,client,access.IsAdministrator,access.Workspaces,overdue,AllowedJobs());
    }
    public Guid SaveTask(Guid client,Guid? id,CrmTaskRequest r)
    {
        access.Client(client,CrmPermissions.Tasks);access.Workspace(r.WorkspaceId);CrmRules.Validate(r);
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        if(Scalar(c,tx,"select user_name from studio_workspace_members where workspace_id=$1 and lower(user_name)=lower($2) and is_active and access_workspace",r.WorkspaceId,r.AssignedTo) is null)throw new ArgumentException("Task owner must be an active member of its workspace.");
        if(r.JobId is {} job&&(Scalar(c,tx,"select id from studio_jobs where id=$1 and client_id=$2 and workspace_id=$3",job,client,r.WorkspaceId) is null||!AllowedJobs().Contains(job)))throw new ArgumentException("Choose an accessible job belonging to this client and workspace.");
        if(r.EnquiryId is {} enquiry&&Scalar(c,tx,"select id from studio_conversations where id=$1 and client_id=$2 and workspace_id=$3",enquiry,client,r.WorkspaceId) is null)throw new ArgumentException("Choose an enquiry belonging to this client and workspace.");
        Guid task=id??Guid.NewGuid();
        if(id.HasValue){using var q=new NpgsqlCommand("select workspace_id,job_id from studio_crm_tasks where id=$1 and client_id=$2 for update",c,tx);q.Parameters.AddWithValue(task);q.Parameters.AddWithValue(client);using var reader=q.ExecuteReader();if(!reader.Read())throw new KeyNotFoundException();access.Workspace(reader.GetGuid(0));if(!reader.IsDBNull(1)&&!AllowedJobs().Contains(reader.GetGuid(1)))throw new UnauthorizedAccessException();}
        Execute(c,tx,"""
            insert into studio_crm_tasks(id,client_id,workspace_id,job_id,enquiry_id,assigned_to,task_type,priority,status,due_at_utc,notes,completed_at_utc,created_by)
            values($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,case when $9='Completed' then now() end,$12)
            on conflict(id) do update set workspace_id=excluded.workspace_id,job_id=excluded.job_id,enquiry_id=excluded.enquiry_id,assigned_to=excluded.assigned_to,
            task_type=excluded.task_type,priority=excluded.priority,status=excluded.status,due_at_utc=excluded.due_at_utc,notes=excluded.notes,
            completed_at_utc=case when excluded.status='Completed' then coalesce(studio_crm_tasks.completed_at_utc,now()) end,updated_at_utc=now()
            """,task,client,r.WorkspaceId,r.JobId,r.EnquiryId,r.AssignedTo,r.TaskType,r.Priority,r.Status,r.DueAtUtc.ToUniversalTime(),r.Notes,Actor);
        Audit(c,tx,client,r.WorkspaceId,r.Status=="Completed"?"FollowUpCompleted":id.HasValue?"FollowUpUpdated":"FollowUpScheduled",r.TaskType+" · "+r.Status+" · "+r.AssignedTo,Actor,task);tx.Commit();return task;
    }
    public void Lead(Guid client,Guid enquiry,CrmLeadRequest r)
    {
        access.Client(client,CrmPermissions.Edit);if(!CrmRules.LeadStatuses.Contains(r.Status)||r.Metadata.ValueKind!=JsonValueKind.Object||r.Metadata.GetRawText().Length>10000)throw new ArgumentException("Choose a supported lead status and metadata.");
        if(r.Status=="Lost"&&(!r.Metadata.TryGetProperty("lostReason",out var reason)||string.IsNullOrWhiteSpace(reason.GetString())))throw new ArgumentException("A lost reason is required.");
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        var workspace=Scalar(c,tx,"select workspace_id from studio_conversations where id=$1 and client_id=$2 for update",enquiry,client) as Guid?;
        if(workspace is null)throw new KeyNotFoundException();access.Workspace(workspace.Value);
        Execute(c,tx,"update studio_conversations set lead_status=$2,crm_metadata=$3::jsonb where id=$1",enquiry,r.Status,r.Metadata.GetRawText());
        Audit(c,tx,client,workspace,"LeadStatusChanged","Lead status changed to "+r.Status,Actor,enquiry);tx.Commit();
    }
}
