using System.Security.Claims;
using System.Text.Json;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Npgsql;
namespace Aegis.Studio.Web.Workflow;

public static class WorkflowEndpoints
{
    public static void MapConnectedWorkflow(this WebApplication app)
    {
        app.MapGet("/api/workflow/jobs",(ClaimsPrincipal user,WorkflowStore store)=>Results.Ok(store.Monitor(user))).RequireAuthorization();
        app.MapGet("/api/jobs/{job:guid}/connected-workflow",(Guid job,ClaimsPrincipal user,WorkflowStore store)=>Run(()=>Results.Ok(store.View(job,user)))).RequireAuthorization();
        app.MapPost("/api/jobs/{job:guid}/connected-workflow",(Guid job,WorkflowCommand request,ClaimsPrincipal user,WorkflowStore store)=>Run(()=>Results.Ok(store.Execute(job,request,user)))).RequireAuthorization();
        app.MapGet("/api/workflow/workspaces",(ClaimsPrincipal user,WorkflowStore workflow,WorkspaceStore store)=>Results.Ok(store.GetAll().Where(w=>w.IsEnabled&&(workflow.IsFrontDesk(user)||workflow.Manager(user,w.Id))))).RequireAuthorization();
        app.MapGet("/api/workflow/assignment-requests",(ClaimsPrincipal user,WorkflowStore store,StudioDatabase db)=>
        {
            using var c=db.OpenConnection();using var q=WorkflowStore.Command(c,null,"select coalesce(jsonb_agg(to_jsonb(r) order by r.created_at_utc desc),'[]'::jsonb)::text from studio_workflow_assignment_requests r where lower(r.user_name)=lower($1) or r.job_id=any($2)",WorkflowStore.Name(user),store.Monitor(user).Select(x=>JsonSerializer.SerializeToElement(x).GetProperty("jobId").GetGuid()).Where(id=>store.CanManage(user,id)).ToArray());
            return Results.Ok(JsonSerializer.Deserialize<JsonElement>((string)q.ExecuteScalar()!));
        }).RequireAuthorization();
        app.MapGet("/api/workflow/transfer-requests",(ClaimsPrincipal user,WorkflowStore store,StudioDatabase db)=>
        {
            using var c=db.OpenConnection();
            using var q=WorkflowStore.Command(c,null,"select id,job_id,source_workspace_id,destination_workspace_id,requested_by,reason,status,created_at_utc from studio_workflow_transfer_requests where status='Pending' order by created_at_utc desc limit 250");
            using var reader=q.ExecuteReader();
            var pending=new List<object>();
            while(reader.Read())
            {
                var destination=reader.GetGuid(3);
                if(!store.Manager(user,destination))continue;
                pending.Add(new{id=reader.GetGuid(0),jobId=reader.GetGuid(1),sourceWorkspaceId=reader.GetGuid(2),destinationWorkspaceId=destination,requestedBy=reader.GetString(4),reason=reader.GetString(5),status=reader.GetString(6),createdAt=reader.GetFieldValue<DateTimeOffset>(7)});
            }
            return Results.Ok(pending);
        }).RequireAuthorization();
        app.MapGet("/api/jobs/{job:guid}/upload-batches",(Guid job,ClaimsPrincipal user,WorkflowStore store,StudioDatabase db)=>Run(()=>
        {
            if(!store.CanManage(user,job))throw new UnauthorizedAccessException();
            using var c=db.OpenConnection();
            using var q=WorkflowStore.Command(c,null,"select coalesce(jsonb_agg(to_jsonb(b) order by b.recorded_at_utc desc),'[]'::jsonb)::text from studio_workflow_upload_batches b where b.job_id=$1",job);
            return Results.Ok(JsonSerializer.Deserialize<JsonElement>((string)q.ExecuteScalar()!));
        })).RequireAuthorization();
        app.MapGet("/api/jobs/{job:guid}/delivery-requirements",(Guid job,ClaimsPrincipal user,WorkflowStore store,StudioDatabase db)=>Run(()=>
        {
            if(!store.CanManage(user,job))throw new UnauthorizedAccessException();
            using var c=db.OpenConnection();
            using var q=WorkflowStore.Command(c,null,"select coalesce(jsonb_agg(to_jsonb(r) order by r.created_at_utc),'[]'::jsonb)::text from studio_workflow_delivery_requirements r where r.job_id=$1",job);
            return Results.Ok(JsonSerializer.Deserialize<JsonElement>((string)q.ExecuteScalar()!));
        })).RequireAuthorization();
        var admin=app.MapGroup("/api/admin/connected-workflow").RequireAuthorization(policy=>policy.RequireRole("Administrator"));
        admin.MapGet("/configuration",(StudioDatabase db)=>
        {
            using var c=db.OpenConnection();return Results.Ok(JsonSerializer.Deserialize<JsonElement>((string)WorkflowStore.Scalar(c,null,"select jsonb_build_object('thresholds',coalesce((select jsonb_agg(to_jsonb(t)) from studio_workflow_thresholds t),'[]'::jsonb),'audiences',coalesce((select jsonb_agg(to_jsonb(a)) from studio_workflow_audiences a),'[]'::jsonb),'failures',coalesce((select jsonb_agg(to_jsonb(f)) from studio_workflow_delivery_failures f),'[]'::jsonb),'reconciliation',coalesce((select jsonb_agg(to_jsonb(r)) from studio_workflow_reconciliation r),'[]'::jsonb),'timingPolicy','Elapsed','timezone','America/Jamaica')::text")!));
        });
        // Read-only reset inventory. No destructive endpoint is exposed until the
        // complete dependency-aware cleanup and demo seeder are implemented.
        admin.MapGet("/demo-data/preview",(StudioDatabase db)=>Run(()=>
        {
            using var c=db.OpenConnection();
            // Metadata only: never execute a destructive operation from an inventory endpoint.
            using var q=new NpgsqlCommand(@"select t.table_name,
                (select count(*) from information_schema.columns col where col.table_schema='public' and col.table_name=t.table_name) as columns,
                (select count(*) from pg_constraint fk
                   join pg_class child on child.oid=fk.conrelid
                   join pg_namespace ns on ns.oid=child.relnamespace
                  where fk.contype='f' and ns.nspname='public'
                    and fk.confrelid=to_regclass('public.'||quote_ident(t.table_name))) as inbound_foreign_keys
                from information_schema.tables t
                where t.table_schema='public' and t.table_type='BASE TABLE'
                  and t.table_name like 'studio_%'
                order by t.table_name",c);
            using var reader=q.ExecuteReader();
            var tables=new List<object>();
            while(reader.Read())tables.Add(new {table=reader.GetString(0),columns=reader.GetInt64(1),inboundForeignKeys=reader.GetInt64(2)});
            return Results.Ok(new {readyForReset=false,requiresExplicitDemoEnvironment=true,
                warning="Inventory only: foreign-key dependencies are not a deletion plan. Business-data classification and storage ownership must be verified before reset.",
                tables});
        }));
        admin.MapPost("/demo-data/generate",(StudioDatabase db,ClaimsPrincipal user,IWebHostEnvironment environment,IConfiguration config)=>Run(()=>
        {
            if(!environment.IsDevelopment()&&!config.GetValue("DemoData:Enabled",false))return Results.NotFound();
            return Results.Ok(ConnectedWorkflowDemo.Generate(db,WorkflowStore.Name(user)));
        }));
        admin.MapPost("/demo-data/remove",(StudioDatabase db,IWebHostEnvironment environment,IConfiguration config,DemoResetConfirmation request)=>Run(()=>
        {
            if(!config.GetValue("DemoData:AllowDestructiveReset",false))return Results.NotFound();
            if(!environment.IsDevelopment()&&!config.GetValue("DemoData:Enabled",false))return Results.NotFound();
            if(request.Confirmation!="REMOVE STUDIO DEMO DATA")return Results.BadRequest(new{error="Explicit confirmation is required."});
            return Results.Ok(ConnectedWorkflowDemo.Reset(db));
        }));
        admin.MapPost("/demo-data/reset-and-generate",(StudioDatabase db,ClaimsPrincipal user,IWebHostEnvironment environment,IConfiguration config,DemoResetConfirmation request)=>Run(()=>
        {
            if(!config.GetValue("DemoData:AllowDestructiveReset",false))return Results.NotFound();
            if(!environment.IsDevelopment()&&!config.GetValue("DemoData:Enabled",false))return Results.NotFound();
            if(request.Confirmation!="REMOVE STUDIO DEMO DATA")return Results.BadRequest(new{error="Explicit confirmation is required."});
            return Results.Ok(ConnectedWorkflowDemo.ResetAndGenerate(db,WorkflowStore.Name(user)));
        }));
        admin.MapPut("/thresholds",(WorkflowThreshold request,ClaimsPrincipal user,StudioDatabase db)=>Run(()=>
        {
            WorkflowRules.ValidateThreshold(request);using var c=db.OpenConnection();using var tx=c.BeginTransaction();
            WorkflowStore.Sql(c,tx,"insert into studio_workflow_thresholds values($1,$2,$3) on conflict(stage) do update set warning_hours=excluded.warning_hours,overdue_hours=excluded.overdue_hours",request.Stage,request.WarningHours,request.OverdueHours);
            WorkflowStore.Sql(c,tx,"insert into studio_workflow_configuration_audit(id,action,actor,detail) values($1,'WorkflowThreshold',$2,$3::jsonb)",Guid.NewGuid(),WorkflowStore.Name(user),JsonSerializer.Serialize(request));tx.Commit();return Results.NoContent();
        }));
        admin.MapPut("/audiences",(WorkflowAudience request,StudioDatabase db)=>Run(()=>
        {
            if(request.Audience is not ("Front Desk" or "Production"))throw new ArgumentException("Choose Front Desk or Production.");
            if(request.Users.Any(x=>!new StudioEmployeeDirectoryStore(db).IsAssignable(x)))throw new ArgumentException("Choose active Studio employees.");
            using var c=db.OpenConnection();using var tx=c.BeginTransaction();WorkflowStore.Sql(c,tx,"delete from studio_workflow_audiences where audience=$1",request.Audience);
            foreach(var name in request.Users.Distinct(StringComparer.OrdinalIgnoreCase))WorkflowStore.Sql(c,tx,"insert into studio_workflow_audiences values($1,$2)",request.Audience,name.Trim().ToLowerInvariant());tx.Commit();return Results.NoContent();
        }));
    }
    internal static IResult Run(Func<IResult> action)
    {try{return action();}catch(WorkflowConflictException e){return Results.Conflict(new{error=e.Message});}catch(UnauthorizedAccessException){return Results.Forbid();}catch(KeyNotFoundException){return Results.NotFound();}catch(ArgumentException e){return Results.BadRequest(new{error=e.Message});}}
}
public sealed record WorkflowAudience(string Audience,IReadOnlyList<string> Users);

// Timing/configuration recovery only. Delivery remains in the existing NotificationDeliveryWorker.
public sealed class WorkflowMonitorWorker(IServiceScopeFactory scopes,ILogger<WorkflowMonitorWorker> log):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                using var scope=scopes.CreateScope();var db=scope.ServiceProvider.GetRequiredService<StudioDatabase>();using var c=db.OpenConnection();using var tx=c.BeginTransaction();
                if(!(bool)WorkflowStore.Scalar(c,tx,"select pg_try_advisory_xact_lock(hashtextextended('studio-workflow-monitor',0))")!){tx.Rollback();continue;}
                // Unknown historical clocks do not trigger alerts; scheduled clocks use upcoming session deadlines.
                var rows=new List<(Guid Job,string Key,string Stage)>();
                using(var q=WorkflowStore.Command(c,tx,"""
                    select j.id,j.status,j.stage_entered_at_utc from studio_jobs j left join studio_workflow_thresholds t on t.stage=j.status
                    where j.stage_entered_at_utc is not null and j.status not in ('Completed','Cancelled') and
                    ((j.status not in ('Scheduled','Photography In Progress') and j.stage_entered_at_utc+(t.overdue_hours*interval '1 hour')<=now()) or
                     (j.status in ('Scheduled','Photography In Progress') and exists(select 1 from studio_workflow_sessions ws join studio_shoot_sessions s on s.id=ws.shoot_id where ws.job_id=j.id and ws.required and s.checked_out_at_utc is null and s.status<>'Cancelled' and s.planned_end_utc+interval '4 hours'<=now())))
                    """))using(var r=q.ExecuteReader()){while(r.Read())rows.Add((r.GetGuid(0),"overdue:"+r.GetGuid(0)+":"+r.GetFieldValue<DateTimeOffset>(2).ToUnixTimeMilliseconds(),r.GetString(1)));}
                foreach(var row in rows)WorkflowStore.Notify(c,tx,row.Job,row.Key,row.Stage=="Pending"?"Front Desk":row.Stage is "Awaiting Production Review" or "Active Production" or "Awaiting Approval"?"Production":"Manager","Overdue Job: "+row.Stage);
                var failures=new List<(Guid Job,string Key,string Audience,string Subject,string Body)>();
                using(var q=WorkflowStore.Command(c,tx,"select job_id,event_key,audience,subject,body from studio_workflow_delivery_failures order by created_at_utc limit 100"))using(var r=q.ExecuteReader()){while(r.Read())failures.Add((r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4)));}
                foreach(var f in failures)WorkflowStore.Sql(c,tx,"select studio_workflow_notify($1,$2,$3,$4,$5)",f.Job,f.Key,f.Audience,f.Subject,f.Body);
                // A recipient may lose their employment or email permission after queuing.
                // Cancel unsent workflow email before the existing delivery worker leases it.
                WorkflowStore.Sql(c,tx,"""
                    update studio_notifications n set status='Cancelled'
                    where n.workflow_event_key is not null and n.channel='Email' and n.status='Pending'
                      and (exists(select 1 from studio_employees e where lower(e.user_name)=lower(n.user_name) and not e.is_active)
                        or not exists(select 1 from studio_user_profiles p where lower(p.user_name)=lower(n.user_name)
                          and p.email_enabled and p.email is not null and lower(p.email)=lower(n.destination)))
                    """);
                tx.Commit();
            }
            catch(Exception e)when(!stoppingToken.IsCancellationRequested){log.LogError(e,"Workflow timing/recipient recovery failed; durable records retained for retry");}
        }while(await timer.WaitForNextTickAsync(stoppingToken));
    }
}

public sealed record DemoResetConfirmation(string Confirmation);
