using Aegis.Studio.Web.Galleries;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Production;
using Npgsql;

namespace Aegis.Studio.Web.Workflow;

public sealed record CrewChange(string UserName,string Role,string? Notes=null,DateTimeOffset? Start=null,DateTimeOffset? End=null);
public sealed record WorkflowCommand(string Action,long ExpectedRevision,Guid OperationId,string? UserName=null,string? Reason=null,Guid? SessionId=null,string? Title=null,IReadOnlyList<CrewChange>? Crew=null,DateTimeOffset? Start=null,DateTimeOffset? End=null,string? Location=null,bool AllowConflicts=false,bool Required=true,Guid? WorkspaceId=null,Guid? TaskId=null,string? Kind=null,string? Method=null,string? Reference=null,bool BackupConfirmed=false,string? TargetStage=null,Guid? AssignmentRequestId=null,string? Status=null,string? Priority=null,DateTimeOffset? ScheduledAtUtc=null,DateTimeOffset? DueAtUtc=null,string? Description=null,string? BlockedReason=null,string? HandoffTo=null,int? ExpectedFiles=null,int? CompletedFiles=null,int? FailedFiles=null);

public sealed class WorkflowStore(StudioDatabase database,JobAccessService access,WorkspaceAccessService workspaces,IClientGalleryPublisher publisher,IConfiguration configuration)
{
    public static string Name(ClaimsPrincipal user)=>user.FindFirstValue("login_name")??user.FindFirstValue(ClaimTypes.NameIdentifier)??user.Identity?.Name??throw new UnauthorizedAccessException();
    public bool IsOperations(ClaimsPrincipal u)=>u.IsInRole("Operations Manager")||u.HasClaim("permission","studio.deposit.override")||new GlobalOperationalRoleStore(database).GetDirectRoleKeysAsync(Name(u)).GetAwaiter().GetResult().Contains("Operations Manager");
    public bool IsFrontDesk(ClaimsPrincipal u)=>u.IsInRole("Administrator")||IsOperations(u)||u.IsInRole("Front Desk")||new GlobalOperationalRoleStore(database).GetDirectRoleKeysAsync(Name(u)).GetAwaiter().GetResult().Contains("Front Desk");
    public bool Manager(ClaimsPrincipal u,Guid workspace)=>u.IsInRole("Administrator")||IsOperations(u)||workspaces.HasPermission(u,workspace,"ManageMembers")||workspaces.HasPermission(u,workspace,"AdminWorkspace");
    public bool CanManage(ClaimsPrincipal u,Guid job)
    {
        using var c=database.OpenConnection();
        using var q=Command(c,null,"select workspace_id,job_lead from studio_jobs where id=$1",job);using var r=q.ExecuteReader();
        return r.Read()&&(Manager(u,r.GetGuid(0))||(!r.IsDBNull(1)&&r.GetString(1).Equals(Name(u),StringComparison.OrdinalIgnoreCase)));
    }
    public object View(Guid job,ClaimsPrincipal u)
    {
        if(!access.HasPermission(u,job,"ViewJob"))throw new UnauthorizedAccessException();
        using var c=database.OpenConnection();return new {data=Snapshot(c,null,job),canManage=CanManage(u,job),canReview=access.HasPermission(u,job,"ManageProduction")&&access.HasPermission(u,job,"ChangeStatus"),canFrontDesk=IsFrontDesk(u),canOverride=IsOperations(u),canAssignOwner=Manager(u,((JsonElement)Snapshot(c,null,job)).GetProperty("workspaceId").GetGuid()),userName=Name(u)};
    }
    public IReadOnlyList<object> Monitor(ClaimsPrincipal u)
    {
        var ids=access.GetAccessibleJobIds(u).ToArray();using var c=database.OpenConnection();
        var result=new List<object>();
        using var q=Command(c,null,"""
            select j.id,j.status,j.workflow_revision,j.stage_entered_at_utc,j.workflow_closed_at_utc,j.job_lead,j.workspace_id,j.blocked_reason,
                   t.warning_hours,t.overdue_hours,(select min(s.planned_end_utc) from studio_shoot_sessions s join studio_workflow_sessions ws on ws.shoot_id=s.id where ws.job_id=j.id and ws.required and s.status<>'Cancelled' and s.checked_out_at_utc is null),
                   coalesce(array(select distinct c.user_name from studio_workflow_crew c join studio_workflow_sessions ws on ws.id=c.session_id where ws.job_id=j.id),'{}')
            from studio_jobs j left join studio_workflow_thresholds t on t.stage=j.status where j.id=any($1)
            """,ids);using var r=q.ExecuteReader();
        while(r.Read())
        {
            var stage=r.GetString(1);DateTimeOffset? entered=r.IsDBNull(3)?null:r.GetFieldValue<DateTimeOffset>(3);
            var timing=WorkflowRules.Timing(stage,entered,DateTimeOffset.UtcNow,r.IsDBNull(8)?null:new(stage,r.GetDouble(8),r.GetDouble(9)),r.IsDBNull(10)?null:r.GetFieldValue<DateTimeOffset>(10),r.IsDBNull(4)?null:r.GetFieldValue<DateTimeOffset>(4));
            result.Add(new{jobId=r.GetGuid(0),stage,revision=r.GetInt64(2),enteredAt=entered,timing,owner=r.IsDBNull(5)?null:r.GetString(5),workspaceId=r.GetGuid(6),blockedReason=r.IsDBNull(7)?null:r.GetString(7),photographers=r.GetFieldValue<string[]>(11),nextAction=WorkflowRules.NextAction(stage),responsible=stage=="Pending"?"FrontDesk":stage=="Needs Assignment"?"Workspace Manager":stage is "Awaiting Production Review" or "Active Production" or "Awaiting Approval"?"Production":r.IsDBNull(5)?"Workspace Manager":r.GetString(5)});
        }
        return result;
    }
    public JsonElement Execute(Guid job,WorkflowCommand request,ClaimsPrincipal user)
    {
        if(request.OperationId==Guid.Empty||request.ExpectedRevision<0)throw new ArgumentException("Operation ID and current Job revision are required.");
        if((request.Reason?.Length??0)>4000)throw new ArgumentException("Reason is too long.");
        string actor=Name(user),fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actor+JsonSerializer.Serialize(request))));
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        Sql(c,tx,"select pg_advisory_xact_lock(hashtextextended($1,0))","workflow:"+request.OperationId);
        using(var prior=Command(c,tx,"select job_id,fingerprint from studio_workflow_commands where operation_id=$1",request.OperationId))using(var r=prior.ExecuteReader())
        {if(r.Read()){if(r.GetGuid(0)!=job||r.GetString(1)!=fingerprint)throw new ArgumentException("This operation ID belongs to different facts.");r.Close();if(!access.HasPermission(user,job,"ViewJob"))throw new UnauthorizedAccessException();return Snapshot(c,tx,job);}}
        Guid workspace;string stage;string? owner,correction;
        using(var q=Command(c,tx,"select workspace_id,status,job_lead,workflow_revision,correction_kind from studio_jobs where id=$1 for update",job))using(var r=q.ExecuteReader())
        {if(!r.Read())throw new KeyNotFoundException();workspace=r.GetGuid(0);stage=r.GetString(1);owner=r.IsDBNull(2)?null:r.GetString(2);if(r.GetInt64(3)!=request.ExpectedRevision)throw new WorkflowConflictException();correction=r.IsDBNull(4)?null:r.GetString(4);}
        bool manager=Manager(user,workspace),manage=manager||string.Equals(owner,actor,StringComparison.OrdinalIgnoreCase),review=access.HasPermission(user,job,"ManageProduction")&&access.HasPermission(user,job,"ChangeStatus");
        // Destination managers may decide a pending transfer before acquiring Job access.
        bool destinationTransferReviewer=request.Action=="decide-transfer"&&
            Exists(c,tx,"select exists(select 1 from studio_workflow_transfer_requests r where r.id=$1 and r.job_id=$2 and r.status='Pending' and r.source_workspace_id=$3)",request.AssignmentRequestId,job,workspace)&&
            request.AssignmentRequestId.HasValue&&
            CanReviewTransfer(c,tx,user,request.AssignmentRequestId.Value,job);
        if(!destinationTransferReviewer&&!access.HasPermission(user,job,"ViewJob"))throw new UnauthorizedAccessException();
        Sql(c,tx,"select set_config('studio.connected_command','true',true)");
        Sql(c,tx,"select set_config('studio.actor',$1,true),set_config('studio.reason',$2,true)",actor,request.Reason??"");
        bool stateAction=false;
        switch(request.Action)
        {
            case "owner":
                if(!manager)throw new UnauthorizedAccessException();EnsureMutable(stage);
                if(!string.IsNullOrWhiteSpace(request.UserName))RequireEmployee(request.UserName);
                else RequireReason(request);
                Sql(c,tx,"update studio_jobs set job_lead=$2,assigned_to=$2 where id=$1",job,string.IsNullOrWhiteSpace(request.UserName)?null:request.UserName.Trim());
                if(!string.IsNullOrWhiteSpace(request.UserName))Notify(c,tx,job,request.OperationId.ToString(),request.UserName,"Job Owner assigned");
                EnsureDraft(c,tx,job);Recalculate(c,tx,job);break;
            case "session":
                if(!manage)throw new UnauthorizedAccessException();EnsureMutable(stage);
                if(stage is not ("Needs Assignment" or "Needs Photographer Assignment" or "Awaiting Scheduling" or "Scheduled"))throw new ArgumentException("Withdraw/reopen the review or resolve the started shoot before changing its crew.");
                SaveSession(c,tx,job,request,actor,user);Recalculate(c,tx,job);break;
            case "start-session":case "finish-session":
                if(!manage&&!IsCrew(c,tx,job,request.SessionId,actor,true))throw new UnauthorizedAccessException();
                if(stage is not ("Scheduled" or "Photography In Progress"))throw new ArgumentException("Photography can only start or finish at the scheduled photography stages.");
                ExecuteSession(c,tx,job,request);break;
            case "contribution-ready":case "waive-contribution":
                if(stage=="Changes Requested"&&correction!="Source")throw new ArgumentException("Production corrections do not reopen source contributions.");
                string contributor=request.UserName??actor;
                if(!manage&&(request.Action=="waive-contribution"||!contributor.Equals(actor,StringComparison.OrdinalIgnoreCase)))throw new UnauthorizedAccessException();
                if(!IsCrew(c,tx,job,request.SessionId,contributor,false))throw new ArgumentException("Choose a photographer in this Job's session.");
                if(stage is not ("Awaiting Upload" or "Changes Requested"))throw new ArgumentException("Contributions become ready after required photography is completed.");
                if(request.Action=="waive-contribution")RequireReason(request);
                else
                {
                    if(!request.BackupConfirmed)throw new ArgumentException("Confirm the existing required backup checks before declaring readiness.");
                    if(!Exists(c,tx,"select exists(select 1 from studio_asset_versions where job_id=$1)",job))throw new ArgumentException("Upload completed material first.");
                }
                Sql(c,tx,"update studio_workflow_crew set ready=$3,waiver_reason=$4 where session_id=$1 and lower(user_name)=lower($2)",request.SessionId,contributor,request.Action=="contribution-ready",request.Action=="waive-contribution"?request.Reason:null);break;
            case "task-progress":
                EnsureMutable(stage);
                if(request.TaskId is not Guid progressTask)throw new ArgumentException("Choose a task.");
                string? assignee;
                using(var q=Command(c,tx,"select assigned_to from studio_job_activities where id=$1 and job_id=$2 for update",progressTask,job))using(var reader=q.ExecuteReader())
                {if(!reader.Read())throw new KeyNotFoundException();assignee=reader.IsDBNull(0)?null:reader.GetString(0);}
                if(!manage&&!review&&!string.Equals(assignee,actor,StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException();
                if(request.Status is not ("NotStarted" or "Scheduled" or "InProgress" or "Blocked" or "Completed" or "Cancelled" or "WaitingOnCustomer" or "ReworkRequired" or "Partial" or "Failed" or "ReprintRequired" or "ReadyForDelivery" or "Archived"))throw new ArgumentException("Choose a supported task status.");
                if(request.Status=="Blocked"&&string.IsNullOrWhiteSpace(request.BlockedReason))throw new ArgumentException("Record the blocked reason.");
                if(request.Status=="Completed"&&Exists(c,tx,"select exists(select 1 from studio_production_items where activity_id=$1 and qc_state not in ('Passed','Cancelled'))",progressTask))throw new ArgumentException("Pass all required item QC checks first.");
                Sql(c,tx,"update studio_job_activities set status=$3,blocked_reason=$4,started_at_utc=case when $3='InProgress' then coalesce(started_at_utc,now()) else started_at_utc end,completed_at_utc=case when $3='Completed' then now() else null end,updated_at_utc=now() where id=$1 and job_id=$2",progressTask,job,request.Status,request.Status=="Blocked"?request.BlockedReason:null);
                Notify(c,tx,job,request.OperationId.ToString(),"Owner","Task updated: "+request.Status);break;
            case "task":
                if(!manage&&!review)throw new UnauthorizedAccessException();EnsureMutable(stage);
                if(string.IsNullOrWhiteSpace(request.Title)||request.Title.Length>200)throw new ArgumentException("Task title is required (maximum 200 characters).");
                if(!string.IsNullOrWhiteSpace(request.UserName))RequireEmployee(request.UserName);
                Guid task=request.TaskId??Guid.NewGuid();
                if(request.TaskId.HasValue&&Exists(c,tx,"select exists(select 1 from studio_job_activities where id=$1 and job_id<>$2)",task,job))throw new ArgumentException("Task belongs to another Job.");
                Sql(c,tx,"""
                    insert into studio_job_activities(id,job_id,activity_type,title,description,status,priority,assigned_to,created_by,created_at_utc,updated_at_utc,scheduled_at_utc,due_at_utc,location,handoff_to)
                    values($1,$2,$3,$4,$5,'NotStarted',$8,$6,$7,now(),now(),$9,$10,$11,$12)
                    on conflict(id) do update set title=excluded.title,description=excluded.description,assigned_to=excluded.assigned_to,priority=excluded.priority,scheduled_at_utc=excluded.scheduled_at_utc,due_at_utc=excluded.due_at_utc,location=excluded.location,handoff_to=excluded.handoff_to,updated_at_utc=now()
                    """,task,job,request.Kind??"Other",request.Title,request.Description??request.Reason,request.UserName,actor,request.Priority??"Normal",request.ScheduledAtUtc?.ToUniversalTime(),request.DueAtUtc?.ToUniversalTime(),request.Location,request.HandoffTo);
                if(!string.IsNullOrWhiteSpace(request.UserName))Notify(c,tx,job,request.OperationId.ToString(),request.UserName,"Task assigned: "+request.Title);break;
            case "request-assignment":
                EnsureMutable(stage);
                if(request.Kind is not ("Owner" or "Photographer" or "Task"))throw new ArgumentException("Choose Owner, Photographer or Task.");
                if(request.Kind=="Task"&&(!request.TaskId.HasValue||!Exists(c,tx,"select exists(select 1 from studio_job_activities where id=$1 and job_id=$2)",request.TaskId,job)))throw new ArgumentException("Choose a task in this Job.");
                Sql(c,tx,"insert into studio_workflow_assignment_requests(id,job_id,task_id,user_name,kind,note) values($1,$2,$3,$4,$5,$6) on conflict do nothing",request.OperationId,job,request.TaskId,actor,request.Kind,request.Reason);
                Notify(c,tx,job,request.OperationId.ToString(),request.Kind=="Owner"||string.IsNullOrWhiteSpace(owner)?"Manager":"Owner","Assignment requested");break;
            case "decide-request":
                if(!manage)throw new UnauthorizedAccessException();RequireReason(request);
                string requester,requestKind;Guid? requestedTask;
                using(var q=Command(c,tx,"select user_name,kind,task_id from studio_workflow_assignment_requests where id=$1 and job_id=$2 and status='Pending' for update",request.AssignmentRequestId,job))using(var r=q.ExecuteReader())
                {if(!r.Read())throw new ArgumentException("This request is no longer pending.");requester=r.GetString(0);requestKind=r.GetString(1);requestedTask=r.IsDBNull(2)?null:r.GetGuid(2);}
                if(request.Method is not ("Approved" or "Rejected" or "Redirected"))throw new ArgumentException("Choose an assignment decision.");
                if(requestKind=="Owner"&&!manager)throw new UnauthorizedAccessException();
                if(request.Method=="Redirected")
                {
                    RequireEmployee(request.UserName??"");
                    if(!Exists(c,tx,"select exists(select 1 from studio_workspace_members where workspace_id=$1 and lower(user_name)=lower($2) and is_active and (manage_members or admin_workspace))",workspace,request.UserName))throw new ArgumentException("Redirect to a responsible manager in this workspace.");
                    Sql(c,tx,"update studio_workflow_assignment_requests set redirect_to=$2 where id=$1",request.AssignmentRequestId,request.UserName);
                    Notify(c,tx,job,request.OperationId.ToString(),request.UserName!,"Assignment request redirected");
                }
                else
                {
                    if(request.Method=="Approved")
                    {
                        RequireEmployee(requester);
                        if(requestKind=="Owner"){Sql(c,tx,"update studio_jobs set job_lead=$2,assigned_to=$2 where id=$1",job,requester);EnsureDraft(c,tx,job);Recalculate(c,tx,job);}
                        else if(requestKind=="Task")Sql(c,tx,"update studio_job_activities set assigned_to=$2,updated_at_utc=now() where id=$1 and job_id=$3",requestedTask,requester,job);
                        else
                        {
                            if(!request.SessionId.HasValue||!Exists(c,tx,"select exists(select 1 from studio_workflow_sessions where id=$1 and job_id=$2 and shoot_id is null)",request.SessionId,job))throw new ArgumentException("Choose an unscheduled session; edit scheduled crews through Assign Photographers.");
                            Sql(c,tx,"insert into studio_workflow_crew(session_id,user_name,role,assigned_by) values($1,$2,'Additional',$3) on conflict do nothing",request.SessionId,requester,actor);
                        }
                    }
                    Sql(c,tx,"update studio_workflow_assignment_requests set status=$2,decision_by=$3,decision_reason=$4 where id=$1",request.AssignmentRequestId,request.Method,actor,request.Reason);
                }
                Notify(c,tx,job,request.OperationId.ToString(),requester,"Assignment request: "+request.Method);break;
            case "claim":
            case "release-claim":
            case "reassign-claim":
                if(!IsFrontDesk(user)||stage!="Pending")throw new UnauthorizedAccessException();
                string? currentClaim;
                using(var q=Command(c,tx,"select frontdesk_claim from studio_jobs where id=$1",job))
                    currentClaim=q.ExecuteScalar() as string;
                bool ownsClaim=string.Equals(currentClaim,actor,StringComparison.OrdinalIgnoreCase);
                bool supervisor=manager||IsOperations(user)||user.IsInRole("Administrator");
                if(request.Action=="claim")
                {
                    if(!string.IsNullOrWhiteSpace(currentClaim)&&!ownsClaim)throw new WorkflowConflictException();
                    if(request.UserName is not null&&!request.UserName.Equals(actor,StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException();
                    Sql(c,tx,"update studio_jobs set frontdesk_claim=$2 where id=$1",job,actor);
                }
                else if(request.Action=="release-claim")
                {
                    if(!ownsClaim&&!supervisor)throw new UnauthorizedAccessException();
                    if(!string.IsNullOrWhiteSpace(currentClaim))
                    {
                        if(!ownsClaim)RequireReason(request);
                        Sql(c,tx,"update studio_jobs set frontdesk_claim=null where id=$1",job);
                    }
                }
                else
                {
                    if(!supervisor)throw new UnauthorizedAccessException();
                    RequireReason(request);
                    if(string.IsNullOrWhiteSpace(request.UserName))throw new ArgumentException("Choose the Front Desk employee receiving this claim.");
                    RequireEmployee(request.UserName);
                    Sql(c,tx,"update studio_jobs set frontdesk_claim=$2 where id=$1",job,request.UserName.Trim());
                    Notify(c,tx,job,request.OperationId.ToString(),request.UserName.Trim(),"Front Desk booking assigned");
                }
                break;
            case "transfer":
                EnsureMutable(stage);RequireReason(request);
                if(request.WorkspaceId is not Guid destination||destination==workspace||destination==WorkflowRules.UnassignedWorkspaceId&&stage!="Pending")
                    throw new ArgumentException("Choose a different enabled business workspace.");
                if(!Exists(c,tx,"select exists(select 1 from studio_workspaces where id=$1 and is_enabled)",destination))
                    throw new ArgumentException("Workspace is disabled or missing.");
                if(stage=="Pending")
                {
                    if(!IsFrontDesk(user)&&!manager)throw new UnauthorizedAccessException();
                }
                else if(!IsOperations(user))
                {
                    if(!manager)throw new UnauthorizedAccessException();
                    if(!Manager(user,destination))
                    {
                        Sql(c,tx,"insert into studio_workflow_transfer_requests(id,job_id,source_workspace_id,destination_workspace_id,requested_by,reason) values($1,$2,$3,$4,$5,$6)",request.OperationId,job,workspace,destination,actor,request.Reason);
                        Notify(c,tx,job,request.OperationId.ToString(),"Manager","Workspace transfer approval requested");
                        break;
                    }
                }
                ApplyTransfer(c,tx,job,destination);
                Notify(c,tx,job,request.OperationId.ToString(),"Manager","Job transferred");break;
            case "decide-transfer":
                EnsureMutable(stage);RequireReason(request);
                if(request.AssignmentRequestId is not Guid transferId||request.Method is not ("Approved" or "Rejected"))
                    throw new ArgumentException("Choose a pending transfer and approve or reject it.");
                Guid targetWorkspace;
                using(var q=Command(c,tx,"select destination_workspace_id from studio_workflow_transfer_requests where id=$1 and job_id=$2 and source_workspace_id=$3 and status='Pending' for update",transferId,job,workspace))
                {
                    var value=q.ExecuteScalar();
                    if(value is not Guid id)throw new ArgumentException("Transfer request is no longer pending.");
                    targetWorkspace=id;
                }
                if(!Manager(user,targetWorkspace))throw new UnauthorizedAccessException();
                if(request.Method=="Approved")
                {
                    if(!Exists(c,tx,"select exists(select 1 from studio_workspaces where id=$1 and is_enabled)",targetWorkspace))
                        throw new ArgumentException("Destination workspace is disabled.");
                    ApplyTransfer(c,tx,job,targetWorkspace);
                }
                Sql(c,tx,"update studio_workflow_transfer_requests set status=$2,decided_by=$3,decision_reason=$4,decided_at_utc=now() where id=$1",transferId,request.Method,actor,request.Reason);
                Notify(c,tx,job,request.OperationId.ToString(),"Manager","Workspace transfer "+request.Method);
                break;
            case "blocked":
                EnsureMutable(stage);
                if(!manage&&!review)throw new UnauthorizedAccessException();Sql(c,tx,"update studio_jobs set blocked_reason=$2 where id=$1",job,string.IsNullOrWhiteSpace(request.Reason)?null:request.Reason);break;
            case "reopen":
                if(!manager||!WorkflowRules.Inactive(stage))throw new UnauthorizedAccessException();RequireReason(request);
                if(request.TargetStage is null||!WorkflowRules.Stages.Contains(request.TargetStage)||WorkflowRules.Inactive(request.TargetStage))throw new ArgumentException("Choose a valid active reopening stage.");
                // Reopening starts at the assignment/readiness gate; later stages require their normal proofs.
                if(request.TargetStage is not ("Needs Assignment" or "Needs Photographer Assignment" or "Awaiting Scheduling" or "Awaiting Upload"))throw new ArgumentException("Reopen at an assignment, scheduling or upload gate, then use normal review actions.");
                SetStage(c,tx,job,request.TargetStage);if(request.TargetStage!="Awaiting Upload"){EnsureDraft(c,tx,job);Recalculate(c,tx,job);}break;
            case "confirm":
                if(!IsFrontDesk(user))throw new UnauthorizedAccessException();
                if(workspace==WorkflowRules.UnassignedWorkspaceId)throw new ArgumentException("Route the booking to a business workspace before confirmation.");
                bool deposit=Exists(c,tx,"select exists(select 1 from studio_financial_events where job_id=$1 and kind='Deposit' and amount>0)",job);
                if(!deposit)
                {
                    if(!IsOperations(user))throw new UnauthorizedAccessException();RequireReason(request);
                    Sql(c,tx,"insert into studio_workflow_overrides(id,job_id,kind,actor,reason,original_requirement) select $1,$2,'Deposit',$3,$4,coalesce((select (payload->>'requiredDeposit')::numeric from studio_frontdesk_requests where job_id=$2 limit 1),0)",request.OperationId,job,actor,request.Reason);
                }
                Sql(c,tx,"update studio_frontdesk_requests set status='Confirmed' where job_id=$1",job);EnsureDraft(c,tx,job);stateAction=true;break;
            case "record-upload-batch":
                if(!manage)throw new UnauthorizedAccessException();
                if(stage is not ("Awaiting Upload" or "Changes Requested"))throw new ArgumentException("Upload batches are recorded after photography.");
                if(stage=="Changes Requested"&&correction!="Source")throw new ArgumentException("Only source corrections may record upload batches.");
                if(request.SessionId is not Guid uploadSession||!Exists(c,tx,"select exists(select 1 from studio_workflow_sessions where id=$1 and job_id=$2)",uploadSession,job))
                    throw new ArgumentException("Choose a session belonging to this Job.");
                if(request.ExpectedFiles is null||request.ExpectedFiles<1||request.CompletedFiles is null||request.CompletedFiles<0||request.FailedFiles is null||request.FailedFiles<0||request.CompletedFiles+request.FailedFiles>request.ExpectedFiles)
                    throw new ArgumentException("Provide a valid expected, completed and failed file count.");
                Sql(c,tx,"insert into studio_workflow_upload_batches(id,job_id,session_id,expected_files,completed_files,failed_files,backup_confirmed,recorded_by) values($1,$2,$3,$4,$5,$6,$7,$8)",request.OperationId,job,uploadSession,request.ExpectedFiles.Value,request.CompletedFiles.Value,request.FailedFiles.Value,request.BackupConfirmed,actor);
                break;
            case "submit-source":
                if(!manage)throw new UnauthorizedAccessException();ValidateSourceReady(c,tx,job);CreateSubmission(c,tx,job,"Source",actor);stateAction=true;break;
            case "accept-source":case "approve-finished":
                if(!review)throw new UnauthorizedAccessException();ApproveSubmission(c,tx,job,request.Action=="accept-source"?"Source":"Finished",actor,request.Reason);
                if(request.Action=="accept-source")ProductionItems.CreateApprovedBatch(c,tx,job,actor);stateAction=true;break;
            case "submit-finished":
                if(!review)throw new UnauthorizedAccessException();ValidateFinished(c,tx,job);CreateSubmission(c,tx,job,"Finished",actor);stateAction=true;break;
            case "request-changes":
                if(!review)throw new UnauthorizedAccessException();RequireReason(request);
                string kind=stage=="Awaiting Production Review"?"Source":"Production";
                Sql(c,tx,"update studio_jobs set correction_kind=$2 where id=$1",job,kind);
                Sql(c,tx,"update studio_workflow_submissions set status='Changes Requested',reviewed_by=$3,reviewed_at_utc=now(),note=$4 where job_id=$1 and kind=$2 and status='Pending'",job,kind=="Source"?"Source":"Finished",actor,request.Reason);
                if(kind=="Source")Sql(c,tx,"update studio_workflow_crew set ready=false where session_id in(select id from studio_workflow_sessions where job_id=$1) and waiver_reason is null",job);
                stateAction=true;break;
            case "resume-production":
                if(!review)throw new UnauthorizedAccessException();stateAction=true;break;
            case "publish":
                if(!IsFrontDesk(user)&&!manage&&!review)throw new UnauthorizedAccessException();
                if(stage!="Ready for Delivery")throw new ArgumentException("Finished work must be approved before publication.");
                if(configuration["ClientGallery:Provider"]!="Manifest")throw new ArgumentException("Configure the existing gallery provider before publication.");
                Publish(c,tx,job,request.OperationId,actor,publisher);break;
            case "deliver":
                if(!IsFrontDesk(user)&&!manage)throw new UnauthorizedAccessException();
                if(request.Method=="Gallery"&&!Exists(c,tx,"select exists(select 1 from studio_workflow_publications where job_id=$1)",job))throw new ArgumentException("Publish the approved gallery before recording Gallery delivery.");
                if(string.IsNullOrWhiteSpace(request.Method))throw new ArgumentException("Record delivery method and reference.");
                if(Exists(c,tx,"select exists(select 1 from studio_workflow_delivery_requirements where job_id=$1 and required and not waived and delivered_at_utc is null)",job))
                    throw new ArgumentException("Record delivery evidence or an authorized waiver for every required deliverable first.");
                Sql(c,tx,"insert into studio_workflow_deliveries(id,job_id,method,reference,actor) values($1,$2,$3,$4,$5)",request.OperationId,job,request.Method,request.Reference,actor);stateAction=true;break;
            case "close":
                if(!manage)throw new UnauthorizedAccessException();
                if(Exists(c,tx,"select exists(select 1 from studio_job_activities where job_id=$1 and status not in ('Completed','Cancelled','Archived'))",job))throw new ArgumentException("Resolve open required operational tasks before closing.");
                if(Exists(c,tx,"select exists(select 1 from studio_workflow_delivery_requirements where job_id=$1 and required and not waived and delivered_at_utc is null)",job))throw new ArgumentException("Deliver or explicitly waive every required deliverable before closing.");
                stateAction=true;break;
            case "require-delivery":
                if(!manage||stage is not ("Active Production" or "Awaiting Approval" or "Ready for Delivery" or "Delivered"))throw new UnauthorizedAccessException();
                if(string.IsNullOrWhiteSpace(request.Title))throw new ArgumentException("Name the required deliverable.");
                Sql(c,tx,"insert into studio_workflow_delivery_requirements(id,job_id,title,required,created_by) values($1,$2,$3,true,$4)",request.OperationId,job,request.Title.Trim(),actor);
                break;
            case "complete-delivery":
                if(!manage&&!IsFrontDesk(user))throw new UnauthorizedAccessException();
                if(stage is not ("Ready for Delivery" or "Delivered"))throw new ArgumentException("Approve the finished work before delivery.");
                if(request.TaskId is not Guid deliveryId)throw new ArgumentException("Choose a deliverable.");
                if(string.IsNullOrWhiteSpace(request.Method))throw new ArgumentException("Provide the delivery method.");
                if(!Exists(c,tx,"select exists(select 1 from studio_workflow_delivery_requirements where id=$1 and job_id=$2 and not waived and delivered_at_utc is null)",deliveryId,job))
                    throw new ArgumentException("Deliverable is missing or already completed. Refresh this Job.");
                Sql(c,tx,"update studio_workflow_delivery_requirements set delivered_at_utc=now(),delivery_method=$3,delivery_reference=$4,delivered_by=$5 where id=$1 and job_id=$2 and delivered_at_utc is null and not waived",deliveryId,job,request.Method,request.Reference,actor);
                break;
            case "waive-delivery":
                if(!manager)throw new UnauthorizedAccessException();
                RequireReason(request);
                if(request.TaskId is not Guid waiverId)throw new ArgumentException("Choose a deliverable.");
                if(!Exists(c,tx,"select exists(select 1 from studio_workflow_delivery_requirements where id=$1 and job_id=$2 and not waived and delivered_at_utc is null)",waiverId,job))
                    throw new ArgumentException("Deliverable is missing or already completed. Refresh this Job.");
                Sql(c,tx,"update studio_workflow_delivery_requirements set waived=true,waiver_reason=$3,waived_by=$4 where id=$1 and job_id=$2 and delivered_at_utc is null and not waived",waiverId,job,request.Reason,actor);
                break;
            case "cancel":
                if(stage=="Pending"?!IsFrontDesk(user)&&!manager:!manager)throw new UnauthorizedAccessException();RequireReason(request);
                Sql(c,tx,"update studio_schedule_entries set status='Cancelled' where job_id=$1 and id in(select schedule_entry_id from studio_shoot_sessions where job_id=$1 and checked_in_at_utc is null)",job);
                Sql(c,tx,"update studio_shoot_sessions set status='Cancelled' where job_id=$1 and checked_in_at_utc is null",job);
                Sql(c,tx,"update studio_job_activities set status='Cancelled',updated_at_utc=now() where job_id=$1 and status not in ('Completed','Archived','Cancelled')",job);
                Sql(c,tx,"update studio_frontdesk_requests set status='Cancelled' where job_id=$1",job);stateAction=true;break;
            default:throw new ArgumentException("Choose a supported workflow action.");
        }
        if(stateAction)SetStage(c,tx,job,WorkflowRules.Target(stage,request.Action,correction));
        // Even a same-stage crew/readiness edit changes the revision, detecting stale decisions.
        Sql(c,tx,"update studio_jobs set workflow_revision=workflow_revision+1 where id=$1",job);
        Sql(c,tx,"insert into studio_operational_audit(id,job_id,action,user_name,detail,created_at_utc) values($1,$2,$3,$4,$5,now())",request.OperationId,job,"ConnectedWorkflow:"+request.Action,actor,JsonSerializer.Serialize(request));
        Sql(c,tx,"insert into studio_workflow_commands(operation_id,job_id,fingerprint,result_revision) select $1,id,$3,workflow_revision from studio_jobs where id=$2",request.OperationId,job,fingerprint);
        var result=Snapshot(c,tx,job);tx.Commit();return result;
    }
    private bool CanReviewTransfer(NpgsqlConnection c,NpgsqlTransaction tx,ClaimsPrincipal user,Guid transferId,Guid job)
    {
        var destination=Scalar(c,tx,"select destination_workspace_id from studio_workflow_transfer_requests where id=$1 and job_id=$2 and status='Pending'",transferId,job);
        return destination is Guid id&&Manager(user,id);
    }
    private static void ApplyTransfer(NpgsqlConnection c,NpgsqlTransaction tx,Guid job,Guid destination)
    {
        Sql(c,tx,"update studio_jobs set workspace_id=$2 where id=$1",job,destination);
        Sql(c,tx,"update studio_conversations set workspace_id=$2 where job_id=$1",job,destination);
        Sql(c,tx,"update studio_frontdesk_requests set workspace_id=$2,payload=jsonb_set(payload,'{workspaceId}',to_jsonb($2::uuid)) where job_id=$1",job,destination);
        Sql(c,tx,"update studio_workflow_transfer_requests set status='Superseded',decided_at_utc=now() where job_id=$1 and status='Pending'",job);
    }
    private void SaveSession(NpgsqlConnection c,NpgsqlTransaction tx,Guid job,WorkflowCommand req,string actor,ClaimsPrincipal user)
    {
        Guid session=req.SessionId??Guid.NewGuid();Guid? shoot=null;
        using(var q=Command(c,tx,"select job_id,shoot_id from studio_workflow_sessions where id=$1",session))using(var r=q.ExecuteReader())
        {if(r.Read()){if(r.GetGuid(0)!=job)throw new ArgumentException("Session belongs to another Job.");shoot=r.IsDBNull(1)?null:r.GetGuid(1);}}
        if(shoot.HasValue&&Exists(c,tx,"select exists(select 1 from studio_shoot_sessions where id=$1 and checked_in_at_utc is not null)",shoot.Value))throw new ArgumentException("Started photography retains its crew and actual attendance history.");
        if(!req.Required)RequireReason(req);
        var crew=(req.Crew??[]).ToArray();if(crew.Length>100||crew.Select(x=>x.UserName.ToLowerInvariant()).Distinct().Count()!=crew.Length)throw new ArgumentException("Choose distinct crew members (maximum 100).");
        if(crew.Any(x=>x.Role is not ("Lead" or "Additional"))||crew.Count(x=>x.Role=="Lead")>1)throw new ArgumentException("Choose one Lead and Additional photographers.");
        foreach(var person in crew)RequireEmployee(person.UserName);
        bool finalized=crew.Length>0&&crew.Count(x=>x.Role=="Lead")==1;
        Sql(c,tx,"insert into studio_workflow_sessions(id,job_id,title,required,waiver_reason,crew_finalized) values($1,$2,$3,$4,$5,$6) on conflict(id) do update set title=excluded.title,required=excluded.required,waiver_reason=excluded.waiver_reason,crew_finalized=excluded.crew_finalized",session,job,req.Title??"Photography",req.Required,req.Required?null:req.Reason,finalized);
        Sql(c,tx,"delete from studio_workflow_crew where session_id=$1",session);
        foreach(var person in crew)Sql(c,tx,"insert into studio_workflow_crew(session_id,user_name,role,assigned_by,notes,attendance_start_utc,attendance_end_utc) values($1,$2,$3,$4,$5,$6,$7)",session,person.UserName.Trim().ToLowerInvariant(),person.Role,actor,person.Notes,person.Start?.ToUniversalTime(),person.End?.ToUniversalTime());
        if(req.Start.HasValue!=req.End.HasValue)throw new ArgumentException("Supply both session start and end.");
        if(req.Start.HasValue)
        {
            if(!finalized||req.End<=req.Start||string.IsNullOrWhiteSpace(req.Location))throw new ArgumentException("Finalize a crew and choose a valid time and location before scheduling.");
            var resources=new List<(CrewChange Person,Guid Resource)>();
            foreach(var person in crew)
            {
                var resource=Scalar(c,tx,"select schedule_resource_id from studio_photographers where lower(user_name)=lower($1) and is_active",person.UserName);
                if(resource is not Guid id)throw new ArgumentException(person.UserName+" needs an active photographer profile.");resources.Add((person,id));
                if(person.Start.HasValue!=person.End.HasValue||person.Start<req.Start||person.End>req.End||person.End<=person.Start)throw new ArgumentException("Individual attendance must be a valid range inside the session.");
            }
            var previousResources=shoot.HasValue?(Guid[])Scalar(c,tx,"select coalesce(array_agg(er.resource_id),'{}'::uuid[]) from studio_schedule_entry_resources er join studio_shoot_sessions s on s.schedule_entry_id=er.entry_id where s.id=$1",shoot.Value)!:[];
            foreach(var resource in resources.Select(x=>x.Resource).Concat(previousResources).Distinct().Order())Sql(c,tx,"select pg_advisory_xact_lock(hashtextextended($1,0))",resource.ToString());
            Guid schedule=shoot.HasValue?(Guid)Scalar(c,tx,"select schedule_entry_id from studio_shoot_sessions where id=$1",shoot.Value)!:Guid.NewGuid();
            var conflicts=new List<string>();
            foreach(var item in resources)
            {
                var start=(item.Person.Start??req.Start!.Value).ToUniversalTime();var end=(item.Person.End??req.End!.Value).ToUniversalTime();
                if(Exists(c,tx,"select exists(select 1 from studio_schedule_entries e join studio_schedule_entry_resources er on er.entry_id=e.id where er.resource_id=$1 and e.status<>'Cancelled' and e.id<>$2 and coalesce(er.attendance_start_utc,e.starts_at_utc)<$4 and coalesce(er.attendance_end_utc,e.ends_at_utc)>$3)",item.Resource,schedule,start,end)||Exists(c,tx,"select exists(select 1 from studio_photographer_availability where lower(user_name)=lower($1) and availability_type in ('Unavailable','Leave') and starts_at_utc<$3 and ends_at_utc>$2)",item.Person.UserName,start,end))conflicts.Add(item.Person.UserName);
            }
            if(conflicts.Count>0)
            {
                if(!req.AllowConflicts)throw new ArgumentException("Schedule conflicts: "+string.Join(", ",conflicts)+". Change time/staff or use an authorized explicit override.");
                if(!user.HasClaim("permission","studio.schedule.override"))throw new UnauthorizedAccessException();RequireReason(req);
                Sql(c,tx,"insert into studio_workflow_overrides(id,job_id,kind,actor,reason) values($1,$2,'Scheduling',$3,$4)",req.OperationId,job,actor,req.Reason+" Conflicts: "+string.Join(", ",conflicts));
            }
            Sql(c,tx,"insert into studio_schedule_entries(id,job_id,title,starts_at_utc,ends_at_utc,location,status,notes,created_at_utc) values($1,$2,$3,$4,$5,$6,'Scheduled',$7,now()) on conflict(id) do update set title=excluded.title,starts_at_utc=excluded.starts_at_utc,ends_at_utc=excluded.ends_at_utc,location=excluded.location,status='Scheduled',notes=excluded.notes",schedule,job,req.Title??"Photography",req.Start.Value.ToUniversalTime(),req.End!.Value.ToUniversalTime(),req.Location,req.Reason);
            Sql(c,tx,"delete from studio_schedule_entry_resources where entry_id=$1",schedule);
            foreach(var item in resources)Sql(c,tx,"insert into studio_schedule_entry_resources(entry_id,resource_id,attendance_start_utc,attendance_end_utc) values($1,$2,$3,$4)",schedule,item.Resource,(item.Person.Start??req.Start.Value).ToUniversalTime(),(item.Person.End??req.End!.Value).ToUniversalTime());
            var lead=crew.Single(x=>x.Role=="Lead").UserName;var additional=crew.FirstOrDefault(x=>x.Role=="Additional")?.UserName;Guid shootId=shoot??Guid.NewGuid();
            Sql(c,tx,"""
                insert into studio_shoot_sessions(id,job_id,schedule_entry_id,photographer_user_name,assistant_user_name,call_time_utc,planned_end_utc,location,status,image_count,backup_confirmed,reshoot_required,handoff_status,created_at_utc,updated_at_utc)
                values($1,$2,$3,$4,$5,$6,$7,$8,'Scheduled',0,false,false,'NotReady',now(),now())
                on conflict(id) do update set photographer_user_name=excluded.photographer_user_name,assistant_user_name=excluded.assistant_user_name,call_time_utc=excluded.call_time_utc,planned_end_utc=excluded.planned_end_utc,location=excluded.location,status='Scheduled',updated_at_utc=now()
                """,shootId,job,schedule,lead,additional,req.Start.Value.ToUniversalTime(),req.End.Value.ToUniversalTime(),req.Location);
            Sql(c,tx,"update studio_workflow_sessions set shoot_id=$2 where id=$1",session,shootId);
        }
        else if(shoot.HasValue)
        {
            Sql(c,tx,"update studio_schedule_entries set status='Cancelled' where id=(select schedule_entry_id from studio_shoot_sessions where id=$1)",shoot.Value);
            Sql(c,tx,"update studio_shoot_sessions set status='Cancelled' where id=$1",shoot.Value);
            Sql(c,tx,"update studio_workflow_sessions set shoot_id=null where id=$1",session);
        }
        foreach(var person in crew)Notify(c,tx,job,req.OperationId.ToString(),person.UserName,"Photographer assignment / schedule updated");
    }
    private static void EnsureMutable(string stage){if(WorkflowRules.Inactive(stage))throw new ArgumentException("Reopen this closed Job before editing assignments.");}
    private void RequireEmployee(string name){if(!new StudioEmployeeDirectoryStore(database).IsAssignable(name))throw new ArgumentException("Choose an active, assignable Studio employee.");}
    private static void RequireReason(WorkflowCommand request){if(string.IsNullOrWhiteSpace(request.Reason))throw new ArgumentException("A reason is required.");}
    private static bool IsCrew(NpgsqlConnection c,NpgsqlTransaction tx,Guid job,Guid? session,string name,bool lead)=>Exists(c,tx,"select exists(select 1 from studio_workflow_crew c join studio_workflow_sessions s on s.id=c.session_id where s.job_id=$1 and s.id=$2 and lower(c.user_name)=lower($3) and (not $4 or c.role='Lead'))",job,session,name,lead);
    private static void EnsureDraft(NpgsqlConnection c,NpgsqlTransaction tx,Guid job)=>Sql(c,tx,"insert into studio_workflow_sessions(id,job_id,title) select $1,$2,'Photography' where not exists(select 1 from studio_workflow_sessions where job_id=$2 and required)",Guid.NewGuid(),job);
    private static void Recalculate(NpgsqlConnection c,NpgsqlTransaction tx,Guid job)=>Sql(c,tx,"""
        update studio_jobs set status=case
         when nullif(trim(job_lead),'') is null then 'Needs Assignment'
         when not exists(select 1 from studio_workflow_sessions where job_id=$1 and required) or exists(select 1 from studio_workflow_sessions where job_id=$1 and required and not crew_finalized) then 'Needs Photographer Assignment'
         when exists(select 1 from studio_workflow_sessions ws left join studio_shoot_sessions s on s.id=ws.shoot_id where ws.job_id=$1 and ws.required and (s.id is null or s.status='Cancelled')) then 'Awaiting Scheduling'
         else 'Scheduled' end
        where id=$1 and status in ('Needs Assignment','Needs Photographer Assignment','Awaiting Scheduling','Scheduled')
        """,job);
    private static void ExecuteSession(NpgsqlConnection c,NpgsqlTransaction tx,Guid job,WorkflowCommand req)
    {
        if(!Exists(c,tx,"select exists(select 1 from studio_workflow_sessions w join studio_shoot_sessions s on s.id=w.shoot_id where w.id=$1 and w.job_id=$2 and s.status<>'Cancelled')",req.SessionId,job))throw new ArgumentException("Choose a scheduled active session.");
        if(req.Action=="start-session")
        {
            Sql(c,tx,"update studio_shoot_sessions set status='InProgress',checked_in_at_utc=coalesce(checked_in_at_utc,now()),updated_at_utc=now() where id=(select shoot_id from studio_workflow_sessions where id=$1) and checked_out_at_utc is null",req.SessionId);
            Sql(c,tx,"update studio_jobs set status='Photography In Progress' where id=$1 and status='Scheduled'",job);
        }
        else
        {
            if(!Exists(c,tx,"select exists(select 1 from studio_shoot_sessions where id=(select shoot_id from studio_workflow_sessions where id=$1) and checked_in_at_utc is not null)",req.SessionId))throw new ArgumentException("Start this session before completing it.");
            Sql(c,tx,"update studio_shoot_sessions set status='ShootComplete',checked_out_at_utc=coalesce(checked_out_at_utc,now()),backup_confirmed=$2,updated_at_utc=now() where id=(select shoot_id from studio_workflow_sessions where id=$1)",req.SessionId,req.BackupConfirmed);
            if(!Exists(c,tx,"select exists(select 1 from studio_workflow_sessions w left join studio_shoot_sessions s on s.id=w.shoot_id where w.job_id=$1 and w.required and (s.id is null or s.status='Cancelled' or s.checked_out_at_utc is null))",job))SetStage(c,tx,job,"Awaiting Upload");
        }
    }
    private static void ValidateSourceReady(NpgsqlConnection c,NpgsqlTransaction tx,Guid job)
    {
        if(!Exists(c,tx,"select exists(select 1 from studio_workflow_sessions where job_id=$1 and required)",job)||Exists(c,tx,"select exists(select 1 from studio_workflow_sessions w left join studio_shoot_sessions s on s.id=w.shoot_id where w.job_id=$1 and w.required and (s.id is null or s.status='Cancelled' or s.checked_out_at_utc is null or exists(select 1 from studio_workflow_crew c where c.session_id=w.id and not c.ready and c.waiver_reason is null)))",job))throw new ArgumentException("Complete all required sessions and photographer contributions, or explicitly waive missing contributions.");
        if(Exists(c,tx,"select exists(select 1 from studio_job_activities where job_id=$1 and activity_type in ('FileIngest','Backup') and status not in ('Completed','Cancelled','Archived'))",job))throw new ArgumentException("Complete the required ingest and backup verification tasks before submission.");
        if(!Exists(c,tx,"select exists(select 1 from studio_media_assets a join studio_asset_versions v on v.job_id=a.job_id and v.asset_key=a.asset_key where a.job_id=$1)",job))throw new ArgumentException("Upload required material before submitting.");
        if(Exists(c,tx,"select exists(select 1 from studio_workflow_sessions s where s.job_id=$1 and s.required and not exists(select 1 from studio_workflow_upload_batches b where b.session_id=s.id and b.expected_files=b.completed_files and b.failed_files=0 and b.backup_confirmed))",job))
            throw new ArgumentException("Each required session needs a verified upload batch with every expected file completed, no failures and backup confirmed.");
    }
    private static void ValidateFinished(NpgsqlConnection c,NpgsqlTransaction tx,Guid job)
    {
        if(Exists(c,tx,"select exists(select 1 from studio_job_activities where job_id=$1 and activity_type in ('Cull','Edit','Retouch','Proof','Print','QC') and status not in ('Completed','Cancelled','Archived'))",job))throw new ArgumentException("Resolve required production tasks first.");
        if(Exists(c,tx,"select exists(select 1 from studio_media_assets a where a.job_id=$1 and (a.approved_version_id is null or exists(select 1 from studio_production_items i where i.asset_id=a.id and i.source_version_id=a.approved_version_id and i.qc_state not in ('Passed','Cancelled'))))",job))throw new ArgumentException("Pass current output quality checks before submission.");
    }
    private static void CreateSubmission(NpgsqlConnection c,NpgsqlTransaction tx,Guid job,string kind,string actor)
    {
        var manifest=Scalar(c,tx,"select coalesce(jsonb_agg(jsonb_build_object('assetId',a.id,'versionId',v.id,'sha256',v.sha256)),'[]'::jsonb)::text from studio_media_assets a join lateral(select id,sha256 from studio_asset_versions v where v.job_id=a.job_id and v.asset_key=a.asset_key order by v.version_number desc limit 1) v on true where a.job_id=$1",job) as string;
        if(string.IsNullOrWhiteSpace(manifest)||manifest=="[]")throw new ArgumentException("No completed media is available.");
        // Store the readiness declarations and explicit waivers as evidence beside the immutable version manifest.
        var evidence=(string)Scalar(c,tx,"select coalesce(jsonb_agg(to_jsonb(c)),'[]'::jsonb)::text from studio_workflow_crew c join studio_workflow_sessions w on w.id=c.session_id where w.job_id=$1 and w.required",job)!;
        Sql(c,tx,"insert into studio_workflow_submissions(id,job_id,kind,revision,manifest,submitted_by,contribution_evidence) select $1,$2,$3,coalesce(max(revision),0)+1,$4::jsonb,$5,$6::jsonb from studio_workflow_submissions where job_id=$2 and kind=$3",Guid.NewGuid(),job,kind,manifest,actor,evidence);
    }
    private static void ApproveSubmission(NpgsqlConnection c,NpgsqlTransaction tx,Guid job,string kind,string actor,string? note)
    {
        var manifest=Scalar(c,tx,"select manifest::text from studio_workflow_submissions where job_id=$1 and kind=$2 and status='Pending' order by revision desc limit 1",job,kind) as string??throw new ArgumentException("No pending review submission exists.");
        // New uploads must be resubmitted, not silently approved through an old manifest.
        if(Exists(c,tx,"select exists(select 1 from studio_media_assets a join lateral(select id from studio_asset_versions where job_id=a.job_id and asset_key=a.asset_key order by version_number desc limit 1) v on true where a.job_id=$1 and not exists(select 1 from jsonb_array_elements($2::jsonb) x where (x->>'assetId')::uuid=a.id and (x->>'versionId')::uuid=v.id))",job,manifest))throw new ArgumentException("Material changed after submission. Request corrections and resubmit the new versions.");
        Sql(c,tx,"update studio_media_assets a set approved_version_id=(x->>'versionId')::uuid from jsonb_array_elements($2::jsonb) x where a.job_id=$1 and a.id=(x->>'assetId')::uuid",job,manifest);
        Sql(c,tx,"update studio_workflow_submissions set status='Approved',reviewed_by=$3,reviewed_at_utc=now(),note=$4 where job_id=$1 and kind=$2 and status='Pending'",job,kind,actor,note);
    }
    private static void Publish(NpgsqlConnection c,NpgsqlTransaction tx,Guid job,Guid operation,string actor,IClientGalleryPublisher publisher)
    {
        Guid submission;string manifest,title;
        using(var q=Command(c,tx,"select s.id,s.manifest::text,j.title from studio_workflow_submissions s join studio_jobs j on j.id=s.job_id where s.job_id=$1 and s.kind='Finished' and s.status='Approved' order by s.revision desc limit 1",job))using(var reader=q.ExecuteReader())
        {if(!reader.Read())throw new ArgumentException("Approve a finished submission before publication.");submission=reader.GetGuid(0);manifest=reader.GetString(1);title=reader.GetString(2);}
        var media=new List<ClientGalleryMedia>();
        using(var q=Command(c,tx,"select a.asset_key,v.id,v.file_name,v.stored_path,v.content_type from jsonb_array_elements($2::jsonb) x join studio_media_assets a on a.id=(x->>'assetId')::uuid join studio_asset_versions v on v.id=(x->>'versionId')::uuid and v.job_id=a.job_id and v.asset_key=a.asset_key where a.job_id=$1 order by a.asset_key",job,manifest))using(var reader=q.ExecuteReader())
        {while(reader.Read())media.Add(new(reader.GetString(0),reader.GetGuid(1),reader.GetString(2),"VersionStore/"+reader.GetString(3),reader.GetString(4)));}
        if(media.Count!=JsonSerializer.Deserialize<JsonElement>(manifest).GetArrayLength())throw new ArgumentException("A pinned approved version is unavailable.");
        var publication=publisher.PublishAsync(new(job,title,media),CancellationToken.None).GetAwaiter().GetResult();
        Sql(c,tx,"insert into studio_workflow_publications(id,job_id,submission_id,reference,provider,actor) values($1,$2,$3,$4,$5,$6)",operation,job,submission,publication.Reference,publication.Provider,actor);
        Notify(c,tx,job,operation.ToString(),"Front Desk","Approved gallery handoff recorded");
    }
    public static JsonElement Snapshot(NpgsqlConnection c,NpgsqlTransaction? tx,Guid job)
    {
        var value=Scalar(c,tx,"""
            select jsonb_build_object('jobId',j.id,'stage',j.status,'revision',j.workflow_revision,'owner',j.job_lead,'workspaceId',j.workspace_id,'enteredAt',j.stage_entered_at_utc,'blockedReason',j.blocked_reason,'correctionKind',j.correction_kind,'claim',j.frontdesk_claim,
             'history',coalesce((select jsonb_agg(to_jsonb(h) order by revision) from studio_workflow_history h where h.job_id=j.id),'[]'::jsonb),
             'sessions',coalesce((select jsonb_agg(to_jsonb(w)||jsonb_build_object('shoot',to_jsonb(s),'crew',coalesce((select jsonb_agg(to_jsonb(c) order by role desc,user_name) from studio_workflow_crew c where c.session_id=w.id),'[]'::jsonb)) order by w.created_at_utc) from studio_workflow_sessions w left join studio_shoot_sessions s on s.id=w.shoot_id where w.job_id=j.id),'[]'::jsonb),
             'submissions',coalesce((select jsonb_agg(to_jsonb(r) order by submitted_at_utc) from studio_workflow_submissions r where r.job_id=j.id),'[]'::jsonb),
             'deliveries',coalesce((select jsonb_agg(to_jsonb(d) order by created_at_utc) from studio_workflow_deliveries d where d.job_id=j.id),'[]'::jsonb))::text from studio_jobs j where j.id=$1
            """,job) as string??throw new KeyNotFoundException();return JsonSerializer.Deserialize<JsonElement>(value);
    }
    internal static void SetStage(NpgsqlConnection c,NpgsqlTransaction tx,Guid job,string stage)=>Sql(c,tx,"update studio_jobs set status=$2 where id=$1 and status is distinct from $2",job,stage);
    internal static void Notify(NpgsqlConnection c,NpgsqlTransaction tx,Guid job,string key,string audience,string subject)=>Sql(c,tx,"select studio_workflow_notify($1,$2,$3,$4,$5)",job,key+":"+audience.ToLowerInvariant(),audience,subject,subject);
    internal static NpgsqlCommand Command(NpgsqlConnection c,NpgsqlTransaction? tx,string sql,params object?[] values)
    {var q=new NpgsqlCommand(sql,c,tx);foreach(var value in values)q.Parameters.AddWithValue(value??DBNull.Value);return q;}
    internal static void Sql(NpgsqlConnection c,NpgsqlTransaction? tx,string sql,params object?[] values){using var q=Command(c,tx,sql,values);q.ExecuteNonQuery();}
    internal static object? Scalar(NpgsqlConnection c,NpgsqlTransaction? tx,string sql,params object?[] values){using var q=Command(c,tx,sql,values);return q.ExecuteScalar();}
    internal static bool Exists(NpgsqlConnection c,NpgsqlTransaction? tx,string sql,params object?[] values)=>(bool)Scalar(c,tx,sql,values)!;
}
