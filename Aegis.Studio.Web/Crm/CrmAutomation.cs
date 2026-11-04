using System.Text.Json;
using Aegis.Studio.Web.Media;
using Aegis.Studio.Web.Persistence;
using Npgsql;
namespace Aegis.Studio.Web.Crm;

public static class CrmAutomation
{
    public static readonly string[] Actions=["CreateFollowUpTask","AssignCrmStaff","UpdateCrmMetadata","ScheduleCrmReminder","FlagNeedsAttention","SendCrmNotification"];
    public static void Execute(StudioDatabase db,Guid client,Guid? job,JsonElement action,string key)
    {
        string Text(string name,string fallback="")=>action.TryGetProperty(name,out var value)?value.GetString()??fallback:fallback;
        using var c=db.OpenConnection();using var tx=c.BeginTransaction();
        int Run(string sql,params object[] args)=>MediaProductionStore.Execute(c,tx,sql,args);
        object? Scalar(string sql,params object[] args)=>MediaProductionStore.Scalar(c,tx,sql,args);
        if(Run("insert into studio_crm_automation_receipts(action_key,client_id) values($1,$2) on conflict do nothing",key,client)==0){tx.Commit();return;}
        Guid? workspace=job.HasValue?Scalar("select workspace_id from studio_jobs where id=$1 and client_id=$2",job.Value,client) as Guid?:Scalar("select primary_workspace_id from studio_clients where id=$1 and archived_at_utc is null",client) as Guid?;
        if(workspace is null)throw new InvalidOperationException("CRM automation needs an active client with a workspace.");
        // Explicit workspace opt-in; absent configuration cannot trigger customer-facing side effects.
        if(Scalar("select id from studio_workspaces where id=$1 and is_enabled and crm_automation_enabled",workspace.Value) is null)throw new InvalidOperationException("CRM automation is disabled for this workspace.");
        string assigned=Text("assignedTo");if(string.IsNullOrWhiteSpace(assigned))assigned=Scalar("select account_owner from studio_clients where id=$1",client) as string??"";
        if(string.IsNullOrWhiteSpace(assigned)||Scalar("select user_name from studio_workspace_members where workspace_id=$1 and lower(user_name)=lower($2) and is_active and access_workspace",workspace.Value,assigned) is null)throw new InvalidOperationException("CRM automation needs an active workspace staff owner.");
        string type=Text("type");
        if(type is "CreateFollowUpTask" or "ScheduleCrmReminder" or "FlagNeedsAttention")
        {
            int minutes=action.TryGetProperty("dueMinutes",out var due)&&due.TryGetInt32(out int number)?Math.Clamp(number,-525600,525600):1440;
            string taskType=Text("taskType","General Follow-up");if(!CrmRules.TaskTypes.Contains(taskType))throw new InvalidOperationException("Unsupported CRM task type.");
            Run("insert into studio_crm_tasks(id,client_id,workspace_id,job_id,assigned_to,task_type,priority,due_at_utc,notes,created_by,automation_key) values(gen_random_uuid(),$1,$2,$3,$4,$5,$6,now()+$7*interval '1 minute',$8,'WORKFLOW',$9) on conflict(automation_key) do nothing",client,workspace.Value,(object?)job??DBNull.Value,assigned,taskType,type=="FlagNeedsAttention"?"High":"Normal",type=="FlagNeedsAttention"?-1:minutes,Text("notes","Automated relationship follow-up"),key);
        }
        else if(type=="AssignCrmStaff")Run("update studio_clients set account_owner=$2,updated_at_utc=now() where id=$1",client,assigned);
        else if(type=="UpdateCrmMetadata")
        {
            string tag=Text("tag");if(string.IsNullOrWhiteSpace(tag)||tag.Length>60)throw new InvalidOperationException("Metadata automation requires a tag of at most 60 characters.");
            Run("update studio_clients set tags=array(select distinct unnest(tags||array[$2])),updated_at_utc=now() where id=$1",client,tag);
        }
        else if(type=="SendCrmNotification")
        {
            // Existing notification store, in-app only. External delivery remains in the configured messaging pipeline.
            Run("insert into studio_notifications(id,user_name,channel,event_type,subject,body,reference_type,reference_id,status,created_at_utc) values(gen_random_uuid(),$1,'InApp','CrmFollowUp',$2,$3,'Client',$4,'Delivered',now())",assigned,Text("subject","Client follow-up"),Text("body","Review the client relationship."),client.ToString());
        }
        else throw new InvalidOperationException("Unsupported CRM action.");
        Run("insert into studio_crm_activity(id,client_id,workspace_id,event_type,description,actor) values(gen_random_uuid(),$1,$2,'AutomationAction',$3,'WORKFLOW')",client,workspace.Value,type+" · "+key);tx.Commit();
    }
}
