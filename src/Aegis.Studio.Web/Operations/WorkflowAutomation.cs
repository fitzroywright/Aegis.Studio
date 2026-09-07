using System.Text.Json;
using Aegis.Studio.Web.Notifications;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Production;
using Aegis.Studio.Web.Scheduling;
using Npgsql;

namespace Aegis.Studio.Web.Operations;

public static class WorkflowAutomationRuntime
{
    private static WorkflowAutomationEngine? engine;
    public static void Initialize(WorkflowAutomationEngine value) => engine = value;
    public static void Publish(string eventType, Guid jobId, object? context = null) => engine?.HandleEvent(eventType, jobId, context);
}

public static class WorkflowAutomationEndpoints
{
    public static void MapStudioWorkflowAutomation(this WebApplication app)
    {
        StudioDatabase database = app.Services.GetRequiredService<StudioDatabase>();
        WorkflowAutomationEngine engine = new(database, app.Services.GetRequiredService<StudioStore>(), app.Services.GetRequiredService<NotificationService>(), app.Services.GetRequiredService<WorkspaceAccessService>(), app.Services.GetRequiredService<JobAccessService>(), app.Services.GetRequiredService<Aegis.Studio.Web.Email.OutboundEmailStore>());
        engine.EnsureSchema();
        WorkflowAutomationRuntime.Initialize(engine);

        RouteGroupBuilder group = app.MapGroup("/api/workflows").RequireAuthorization();
        group.MapGet("/", (HttpContext context) => context.User.IsInRole("Administrator") ? Results.Ok(engine.GetDefinitions()) : Results.Forbid());
        group.MapPost("/", (WorkflowDefinitionRequest request, HttpContext context) => context.User.IsInRole("Administrator") ? Results.Ok(engine.SaveDefinition(null, request)) : Results.Forbid());
        group.MapPut("/{id:guid}", (Guid id, WorkflowDefinitionRequest request, HttpContext context) => context.User.IsInRole("Administrator") ? Results.Ok(engine.SaveDefinition(id, request)) : Results.Forbid());
        group.MapDelete("/{id:guid}", (Guid id, HttpContext context) => { if(!context.User.IsInRole("Administrator")) return Results.Forbid(); engine.DeleteDefinition(id); return Results.NoContent(); });
        group.MapGet("/runs", (HttpContext context, int? count) => context.User.IsInRole("Administrator") ? Results.Ok(engine.GetRuns(count ?? 200)) : Results.Forbid());
        group.MapPost("/{id:guid}/test/{jobId:guid}", (Guid id, Guid jobId, HttpContext context) => { if(!context.User.IsInRole("Administrator")) return Results.Forbid(); return Results.Ok(engine.RunDefinition(id, jobId, "ManualTest", new { manual=true })); });

        CancellationToken token = app.Lifetime.ApplicationStopping;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try { engine.ResumeDueRuns(); }
                catch (Exception exception) { app.Logger.LogError(exception, "Workflow scheduler cycle failed."); }
                try { await Task.Delay(TimeSpan.FromSeconds(15), token); } catch (OperationCanceledException) { break; }
            }
        }, token);
    }
}

public sealed class WorkflowAutomationEngine
{
    private readonly StudioDatabase database;
    private readonly StudioStore studio;
    private readonly NotificationService notifications;
    private readonly WorkspaceAccessService workspaceAccess;
    private readonly JobAccessService jobAccess;
    private readonly Aegis.Studio.Web.Email.OutboundEmailStore outbox;

    public WorkflowAutomationEngine(StudioDatabase database, StudioStore studio, NotificationService notifications, WorkspaceAccessService workspaceAccess, JobAccessService jobAccess, Aegis.Studio.Web.Email.OutboundEmailStore outbox)
    {
        this.database=database; this.studio=studio; this.notifications=notifications; this.workspaceAccess=workspaceAccess; this.jobAccess=jobAccess; this.outbox=outbox;
    }

    public void EnsureSchema()
    {
        using NpgsqlConnection connection=database.OpenConnection(); using NpgsqlCommand command=connection.CreateCommand();
        command.CommandText="""
            create table if not exists studio_workflow_definitions
            (
                id uuid primary key,
                name text not null unique,
                description text null,
                trigger_event text not null,
                conditions_json jsonb not null default '{}'::jsonb,
                actions_json jsonb not null,
                is_enabled boolean not null default true,
                created_at_utc timestamptz not null,
                updated_at_utc timestamptz not null
            );
            create table if not exists studio_workflow_runs
            (
                id uuid primary key,
                workflow_id uuid not null references studio_workflow_definitions(id) on delete cascade,
                job_id uuid not null references studio_jobs(id) on delete cascade,
                trigger_event text not null,
                status text not null,
                action_index integer not null default 0,
                context_json jsonb not null default '{}'::jsonb,
                next_action_at_utc timestamptz null,
                last_error text null,
                created_at_utc timestamptz not null,
                updated_at_utc timestamptz not null,
                completed_at_utc timestamptz null
            );
            create index if not exists ix_studio_workflow_runs_due on studio_workflow_runs(status,next_action_at_utc);
            create table if not exists studio_workflow_run_log
            (
                id uuid primary key,
                run_id uuid not null references studio_workflow_runs(id) on delete cascade,
                action_index integer not null,
                action_type text not null,
                message text not null,
                created_at_utc timestamptz not null
            );
            """; command.ExecuteNonQuery();
    }

    public IReadOnlyList<WorkflowDefinition> GetDefinitions()
    {
        using NpgsqlConnection c=database.OpenConnection();using NpgsqlCommand q=c.CreateCommand();q.CommandText="select id,name,description,trigger_event,conditions_json::text,actions_json::text,is_enabled,created_at_utc,updated_at_utc from studio_workflow_definitions order by name";using NpgsqlDataReader r=q.ExecuteReader();List<WorkflowDefinition> v=[];while(r.Read())v.Add(new(r.GetGuid(0),r.GetString(1),r.IsDBNull(2)?null:r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetBoolean(6),r.GetFieldValue<DateTimeOffset>(7),r.GetFieldValue<DateTimeOffset>(8)));return v;
    }

    public WorkflowDefinition SaveDefinition(Guid? id, WorkflowDefinitionRequest request)
    {
        if(string.IsNullOrWhiteSpace(request.Name)||string.IsNullOrWhiteSpace(request.TriggerEvent)) throw new InvalidOperationException("Workflow name and trigger event are required.");
        ValidateJsonObject(request.ConditionsJson); ValidateActions(request.ActionsJson); Guid workflowId=id??Guid.NewGuid();
        using NpgsqlConnection c=database.OpenConnection();using NpgsqlCommand q=c.CreateCommand();q.CommandText="""
            insert into studio_workflow_definitions(id,name,description,trigger_event,conditions_json,actions_json,is_enabled,created_at_utc,updated_at_utc)
            values($1,$2,$3,$4,$5::jsonb,$6::jsonb,$7,now(),now())
            on conflict(id) do update set name=excluded.name,description=excluded.description,trigger_event=excluded.trigger_event,conditions_json=excluded.conditions_json,actions_json=excluded.actions_json,is_enabled=excluded.is_enabled,updated_at_utc=now()
            """;q.Parameters.AddWithValue(workflowId);q.Parameters.AddWithValue(request.Name.Trim());q.Parameters.AddWithValue((object?)request.Description?.Trim()??DBNull.Value);q.Parameters.AddWithValue(request.TriggerEvent.Trim());q.Parameters.AddWithValue(string.IsNullOrWhiteSpace(request.ConditionsJson)?"{}":request.ConditionsJson);q.Parameters.AddWithValue(request.ActionsJson);q.Parameters.AddWithValue(request.IsEnabled);q.ExecuteNonQuery();return GetDefinitions().First(x=>x.Id==workflowId);
    }

    public void DeleteDefinition(Guid id){using NpgsqlConnection c=database.OpenConnection();using NpgsqlCommand q=c.CreateCommand();q.CommandText="delete from studio_workflow_definitions where id=$1";q.Parameters.AddWithValue(id);q.ExecuteNonQuery();}

    public void HandleEvent(string eventType, Guid jobId, object? context)
    {
        foreach(WorkflowDefinition definition in GetDefinitions().Where(x=>x.IsEnabled&&x.TriggerEvent.Equals(eventType,StringComparison.OrdinalIgnoreCase)))
        {
            if(Matches(definition,jobId,context)) RunDefinition(definition.Id,jobId,eventType,context);
        }
    }

    public WorkflowRun RunDefinition(Guid definitionId, Guid jobId, string triggerEvent, object? context)
    {
        WorkflowDefinition definition=GetDefinitions().FirstOrDefault(x=>x.Id==definitionId)??throw new KeyNotFoundException("Workflow definition was not found.");
        if(studio.GetJob(jobId) is null) throw new KeyNotFoundException("Job was not found.");
        Guid runId=Guid.NewGuid();string contextJson=JsonSerializer.Serialize(context??new{});
        using(NpgsqlConnection c=database.OpenConnection()){using NpgsqlCommand q=c.CreateCommand();q.CommandText="insert into studio_workflow_runs(id,workflow_id,job_id,trigger_event,status,action_index,context_json,created_at_utc,updated_at_utc) values($1,$2,$3,$4,'Running',0,$5::jsonb,now(),now())";q.Parameters.AddWithValue(runId);q.Parameters.AddWithValue(definitionId);q.Parameters.AddWithValue(jobId);q.Parameters.AddWithValue(triggerEvent);q.Parameters.AddWithValue(contextJson);q.ExecuteNonQuery();}
        Execute(runId);return GetRun(runId)!;
    }

    public void ResumeDueRuns()
    {
        using NpgsqlConnection c=database.OpenConnection();using NpgsqlCommand q=c.CreateCommand();q.CommandText="select id from studio_workflow_runs where status='Waiting' and next_action_at_utc<=now() order by next_action_at_utc limit 50";using NpgsqlDataReader r=q.ExecuteReader();List<Guid> ids=[];while(r.Read())ids.Add(r.GetGuid(0));foreach(Guid id in ids)Execute(id);
    }

    public IReadOnlyList<WorkflowRun> GetRuns(int count)
    {
        using NpgsqlConnection c=database.OpenConnection();using NpgsqlCommand q=c.CreateCommand();q.CommandText="select id,workflow_id,job_id,trigger_event,status,action_index,context_json::text,next_action_at_utc,last_error,created_at_utc,updated_at_utc,completed_at_utc from studio_workflow_runs order by created_at_utc desc limit $1";q.Parameters.AddWithValue(Math.Clamp(count,1,1000));using NpgsqlDataReader r=q.ExecuteReader();List<WorkflowRun> v=[];while(r.Read())v.Add(ReadRun(r));return v;
    }

    private WorkflowRun? GetRun(Guid id)=>GetRuns(1000).FirstOrDefault(x=>x.Id==id);

    private void Execute(Guid runId)
    {
        WorkflowRun run=GetRun(runId)??throw new KeyNotFoundException("Workflow run not found.");WorkflowDefinition definition=GetDefinitions().First(x=>x.Id==run.WorkflowId);JsonElement actions=JsonDocument.Parse(definition.ActionsJson).RootElement;
        SetRun(runId,"Running",run.ActionIndex,null,null,false);
        for(int i=run.ActionIndex;i<actions.GetArrayLength();i++)
        {
            JsonElement action=actions[i];string type=action.GetProperty("type").GetString()??string.Empty;
            try
            {
                if(type.Equals("WaitMinutes",StringComparison.OrdinalIgnoreCase))
                {
                    int minutes=action.TryGetProperty("minutes",out JsonElement m)?Math.Clamp(m.GetInt32(),1,525600):1;Log(runId,i,type,$"Waiting {minutes} minute(s).");SetRun(runId,"Waiting",i+1,DateTimeOffset.UtcNow.AddMinutes(minutes),null,false);return;
                }
                ExecuteAction(run.JobId,type,action);Log(runId,i,type,"Completed");SetRun(runId,"Running",i+1,null,null,false);
            }
            catch(Exception exception){Log(runId,i,type,"Failed: "+exception.Message);SetRun(runId,"Failed",i,null,exception.Message,false);return;}
        }
        SetRun(runId,"Completed",actions.GetArrayLength(),null,null,true);
    }

    private void ExecuteAction(Guid jobId,string type,JsonElement action)
    {
        switch(type.ToLowerInvariant())
        {
            case "createactivity":
                string activityType=Text(action,"activityType","Other");string title=Text(action,"title",activityType);string? assigned=OptionalText(action,"assignedTo");int? dueMinutes=OptionalInt(action,"dueMinutes");new OperationalWorkStore(database).CreateActivity(jobId,new JobActivityRequest(activityType,title,assigned,"NotStarted",Text(action,"priority","Normal"),null,dueMinutes.HasValue?DateTimeOffset.UtcNow.AddMinutes(dueMinutes.Value):null,null,null,null,null),"WORKFLOW");if(!string.IsNullOrWhiteSpace(assigned))jobAccess.EnsureAssignedMember(jobId,assigned);break;
            case "changejobstatus":
                StudioJob before=studio.GetJob(jobId)??throw new KeyNotFoundException("Job not found.");string status=Text(action,"status",before.Status);StudioJob after=studio.UpdateJob(jobId,new UpdateJobRequest(status,null,null));PublishJob(jobId,"JobStatusChanged",$"Job status: {after.Title}",$"{before.Status} → {after.Status}",true);break;
            case "assignuser":
                string userName=Text(action,"userName",string.Empty);if(string.IsNullOrWhiteSpace(userName))throw new InvalidOperationException("AssignUser requires userName.");StudioJob current=studio.GetJob(jobId)??throw new KeyNotFoundException("Job not found.");studio.UpdateJob(jobId,new UpdateJobRequest(null,userName,null));jobAccess.EnsureAssignedMember(jobId,userName);notifications.PublishToUser(userName,"JobAssigned",$"Job assigned: {current.Title}",$"You were assigned by workflow automation.","Job",jobId.ToString("D"));break;
            case "notifyjob": PublishJob(jobId,"WorkflowNotification",Text(action,"subject","Workflow notification"),Text(action,"body","Workflow automation generated this notification."),false);break;
            case "notifyrequester": PublishJob(jobId,"WorkflowRequester",Text(action,"subject","Job update"),Text(action,"body","There is an update to your request."),true,true);break;
            case "createschedule":
                DateTimeOffset start=DateTimeOffset.UtcNow.AddMinutes(OptionalInt(action,"startsInMinutes")??0);int duration=Math.Max(1,OptionalInt(action,"durationMinutes")??60);new ScheduleStore(database).Create(new ScheduleEntryRequest(jobId,null,Text(action,"title","Scheduled work"),start,start.AddMinutes(duration),OptionalText(action,"location"),"Scheduled",OptionalText(action,"notes"),[]));break;
            default: throw new InvalidOperationException($"Unsupported workflow action '{type}'.");
        }
    }

    private void PublishJob(Guid jobId,string eventType,string subject,string body,bool requesterEligible,bool forceRequester=false)
    {
        StudioJob? job=studio.GetJob(jobId);if(job is null)return;HashSet<string> recipients=new(StringComparer.OrdinalIgnoreCase);foreach(JobMember member in jobAccess.GetMembers(jobId).Where(x=>x.IsActive))recipients.Add(member.UserName);foreach(WorkspaceMember member in workspaceAccess.GetMembers(job.WorkspaceId).Where(x=>x.IsActive))recipients.Add(member.UserName);foreach(string user in recipients)notifications.PublishToUser(user,eventType,subject,body,"Job",jobId.ToString("D"));
        if(forceRequester||requesterEligible){StudioConversation? conversation=studio.GetConversation(job.ConversationId);if(conversation is not null&&conversation.Channel.Equals("Email",StringComparison.OrdinalIgnoreCase)&&conversation.ContactAddress.Contains('@'))outbox.QueueNotification(conversation,subject,body);}
    }

    private bool Matches(WorkflowDefinition definition,Guid jobId,object? context)
    {
        StudioJob? job=studio.GetJob(jobId);if(job is null)return false;JsonElement conditions=JsonDocument.Parse(definition.ConditionsJson).RootElement;if(conditions.ValueKind!=JsonValueKind.Object)return true;if(conditions.TryGetProperty("workspaceId",out JsonElement workspace)&&Guid.TryParse(workspace.GetString(),out Guid workspaceId)&&job.WorkspaceId!=workspaceId)return false;if(conditions.TryGetProperty("jobStatus",out JsonElement status)&&!job.Status.Equals(status.GetString(),StringComparison.OrdinalIgnoreCase))return false;if(conditions.TryGetProperty("assignedTo",out JsonElement assigned)&&!string.Equals(job.AssignedTo,assigned.GetString(),StringComparison.OrdinalIgnoreCase))return false;return true;
    }

    private void SetRun(Guid id,string status,int actionIndex,DateTimeOffset? next,string? error,bool completed){using NpgsqlConnection c=database.OpenConnection();using NpgsqlCommand q=c.CreateCommand();q.CommandText="update studio_workflow_runs set status=$2,action_index=$3,next_action_at_utc=$4,last_error=$5,updated_at_utc=now(),completed_at_utc=case when $6 then now() else completed_at_utc end where id=$1";q.Parameters.AddWithValue(id);q.Parameters.AddWithValue(status);q.Parameters.AddWithValue(actionIndex);q.Parameters.AddWithValue((object?)next??DBNull.Value);q.Parameters.AddWithValue((object?)error??DBNull.Value);q.Parameters.AddWithValue(completed);q.ExecuteNonQuery();}
    private void Log(Guid runId,int index,string type,string message){using NpgsqlConnection c=database.OpenConnection();using NpgsqlCommand q=c.CreateCommand();q.CommandText="insert into studio_workflow_run_log(id,run_id,action_index,action_type,message,created_at_utc) values($1,$2,$3,$4,$5,now())";q.Parameters.AddWithValue(Guid.NewGuid());q.Parameters.AddWithValue(runId);q.Parameters.AddWithValue(index);q.Parameters.AddWithValue(type);q.Parameters.AddWithValue(message);q.ExecuteNonQuery();}
    private static void ValidateJsonObject(string? value){if(string.IsNullOrWhiteSpace(value))return;JsonElement e=JsonDocument.Parse(value).RootElement;if(e.ValueKind!=JsonValueKind.Object)throw new InvalidOperationException("Conditions must be a JSON object.");}
    private static void ValidateActions(string value){JsonElement e=JsonDocument.Parse(value).RootElement;if(e.ValueKind!=JsonValueKind.Array||e.GetArrayLength()==0)throw new InvalidOperationException("Actions must be a non-empty JSON array.");foreach(JsonElement action in e.EnumerateArray())if(!action.TryGetProperty("type",out _))throw new InvalidOperationException("Every workflow action requires a type.");}
    private static string Text(JsonElement e,string name,string fallback)=>e.TryGetProperty(name,out JsonElement v)&&v.ValueKind==JsonValueKind.String?v.GetString()??fallback:fallback;
    private static string? OptionalText(JsonElement e,string name)=>e.TryGetProperty(name,out JsonElement v)&&v.ValueKind==JsonValueKind.String?v.GetString():null;
    private static int? OptionalInt(JsonElement e,string name)=>e.TryGetProperty(name,out JsonElement v)&&v.TryGetInt32(out int n)?n:null;
    private static WorkflowRun ReadRun(NpgsqlDataReader r)=>new(r.GetGuid(0),r.GetGuid(1),r.GetGuid(2),r.GetString(3),r.GetString(4),r.GetInt32(5),r.GetString(6),r.IsDBNull(7)?null:r.GetFieldValue<DateTimeOffset>(7),r.IsDBNull(8)?null:r.GetString(8),r.GetFieldValue<DateTimeOffset>(9),r.GetFieldValue<DateTimeOffset>(10),r.IsDBNull(11)?null:r.GetFieldValue<DateTimeOffset>(11));
}

public sealed record WorkflowDefinition(Guid Id,string Name,string? Description,string TriggerEvent,string ConditionsJson,string ActionsJson,bool IsEnabled,DateTimeOffset CreatedAtUtc,DateTimeOffset UpdatedAtUtc);
public sealed record WorkflowDefinitionRequest(string Name,string? Description,string TriggerEvent,string ConditionsJson,string ActionsJson,bool IsEnabled=true);
public sealed record WorkflowRun(Guid Id,Guid WorkflowId,Guid JobId,string TriggerEvent,string Status,int ActionIndex,string ContextJson,DateTimeOffset? NextActionAtUtc,string? LastError,DateTimeOffset CreatedAtUtc,DateTimeOffset UpdatedAtUtc,DateTimeOffset? CompletedAtUtc);
