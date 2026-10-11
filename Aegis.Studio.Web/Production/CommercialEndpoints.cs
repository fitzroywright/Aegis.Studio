using System.Security.Claims;
using System.Text.Json;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Workflow;
using static Aegis.Studio.Web.Production.CommercialTemplateStore;

namespace Aegis.Studio.Web.Production;

public sealed record SaveCommercialTemplate(CommercialTemplate Definition, int ExpectedVersion = 0, bool Active = true);
public sealed record WorkOrderSource(Guid AssetId);
public sealed record WorkOrderResolution(string Reason);
public sealed record WorkOrderAmendment(string Specifications,string Reason,string? ExpectedSpecifications);

public static class CommercialEndpoints
{
    public static void MapCommercialProduction(this WebApplication app)
    {
        var db = app.Services.GetRequiredService<StudioDatabase>(); CommercialSchema.Ensure(db);
        var admin = app.MapGroup("/api/admin/commercial/templates").RequireAuthorization(policy => policy.RequireRole("Administrator"));
        admin.MapGet("/{kind}", (string kind, StudioDatabase database) => Results.Ok(new CommercialTemplateStore(database).List(kind, true)));
        admin.MapPost("/{kind}", (string kind, SaveCommercialTemplate request, ClaimsPrincipal user, StudioDatabase database) => Run(() => Results.Ok(new CommercialTemplateStore(database).Save(null, kind, request.Definition, 0, request.Active, WorkflowStore.Name(user)))));
        admin.MapPut("/{kind}/{id:guid}", (string kind, Guid id, SaveCommercialTemplate request, ClaimsPrincipal user, StudioDatabase database) => Run(() => Results.Ok(new CommercialTemplateStore(database).Save(id, kind, request.Definition, request.ExpectedVersion, request.Active, WorkflowStore.Name(user)))));
        admin.MapPost("/{id:guid}/duplicate", (Guid id, ClaimsPrincipal user, StudioDatabase database) => Run(() =>
        {
            var store = new CommercialTemplateStore(database); var original = store.Get(id) ?? throw new KeyNotFoundException();
            return Results.Ok(store.Save(null, original.Kind, original.Definition with { Name = original.Definition.Name + " (copy)" }, 0, false, WorkflowStore.Name(user)));
        }));
        app.MapPost("/api/jobs/{jobId:guid}/media/publication",(Guid jobId,WorkflowCommand request,ClaimsPrincipal user,WorkflowStore workflow)=>Run(()=>
        {
            if(request.Action!="publish")throw new ArgumentException("Choose publication.");
            return Results.Ok(workflow.Execute(jobId,request,user));
        })).RequireAuthorization();
        app.MapGet("/api/commercial/templates/{kind}", (string kind, StudioDatabase database) => Results.Ok(new CommercialTemplateStore(database).List(kind))).RequireAuthorization();
        app.MapPost("/api/jobs/{jobId:guid}/commercial/template", (Guid jobId, ApplyJobTemplateRequest request, ClaimsPrincipal user, WorkflowStore workflow, StudioDatabase database) => Run(() =>
        {
            if (!workflow.CanManage(user, jobId) && !workflow.IsFrontDesk(user)) return Results.Forbid();
            new CommercialTemplateStore(database).ApplyToJob(jobId, request, WorkflowStore.Name(user)); return Results.NoContent();
        })).RequireAuthorization();
        app.MapGet("/api/jobs/{jobId:guid}/commercial/agreement", (Guid jobId, ClaimsPrincipal user, JobAccessService access, StudioDatabase database) =>
        {
            if (!access.HasPermission(user, jobId, "ViewJob")) return Results.Forbid();
            using var c = database.OpenConnection(); using var q = Command(c, null, """
                select coalesce(jsonb_agg(jsonb_build_object('id',a.id,'catalogItemId',a.catalog_item_id,'name',p.name,'quantity',a.quantity,
                  'specifications',a.specifications,'unitPrice',coalesce(a.confirmed_unit_price,round(p.unit_price*(1+p.tax_rate/100),2)),
                  'confirmed',a.confirmed_at is not null,'saleLineId',a.sale_line_id)),'[]')::text
                from studio_job_catalog_agreements a join studio_pos_catalog p on p.id=a.catalog_item_id where a.job_id=$1
                """, jobId);
            return Results.Content((string)q.ExecuteScalar()!, "application/json");
        }).RequireAuthorization();
        app.MapGet("/api/production/work-orders", (string? area, Guid? jobId, int? offset, int? limit, bool? includeCompleted, ClaimsPrincipal user, JobAccessService access, StudioDatabase database) => Run(() =>
        {
            int take = Math.Clamp(limit ?? 50, 1, 100), skip = Math.Max(offset ?? 0, 0);
            Guid[] allowed = access.GetAccessibleJobIds(user).Where(id => !jobId.HasValue || id == jobId).ToArray();
            using var c = database.OpenConnection(); using var q = Command(c, null, """
                select coalesce(jsonb_agg(row),'[]')::text from (
                  select w.id,w.job_id as "jobId",j.title as "jobTitle",w.product_name as product,w.quantity,w.original_quantity as "originalQuantity",
                    w.specifications,w.template_version as "templateVersion",w.exception_reason as "exceptionReason",w.asset_id as "assetId",w.source_version_id as "sourceVersionId",
                    count(*) over() as "totalRows",
                    (select jsonb_agg(jsonb_build_object('id',a.id,'title',a.title,'status',a.status,'area',a.production_area,'assignedTo',a.assigned_to,
                       'priority',a.priority,'dueAtUtc',a.due_at_utc,'blockedReason',case when a.blocked_reason is not null then a.blocked_reason
                       when a.approved_image_required and w.source_version_id is null then 'Approved Job Gallery image required'
                       when exists(select 1 from studio_activity_dependencies d join studio_job_activities p on p.id=d.depends_on_id where d.activity_id=a.id and p.status not in ('Completed','Cancelled')) then 'Waiting for prerequisite' end,
                       'qualityControl',a.quality_control_required,'optional',a.optional_step,'step',a.step_definition,'revision',j.workflow_revision) order by a.step_order)
                     from studio_job_activities a where a.work_order_id=w.id) as tasks
                  from studio_catalog_work_orders w join studio_jobs j on j.id=w.job_id
                  where w.job_id=any($1) and ($5 or w.exception_reason is not null or exists(select 1 from studio_job_activities pending where pending.work_order_id=w.id and pending.status not in ('Completed','Cancelled'))) and ($2::text is null or exists(select 1 from studio_job_activities a where a.work_order_id=w.id and a.production_area=$2))
                  order by w.created_at,w.id limit $3 offset $4
                ) row
                """, allowed, string.IsNullOrWhiteSpace(area) ? null : area, take, skip, includeCompleted==true);
            return Results.Content((string)q.ExecuteScalar()!, "application/json");
        })).RequireAuthorization();
        app.MapGet("/api/jobs/{jobId:guid}/commercial/approved-sources",(Guid jobId,string? term,Guid? after,ClaimsPrincipal user,JobAccessService access,StudioDatabase database)=>
        {
            if(!access.HasPermission(user,jobId,"ViewJob"))return Results.Forbid();
            using var c=database.OpenConnection();using var q=Command(c,null,"""
                select coalesce(jsonb_agg(row),'[]')::text from (
                  select a.id,a.asset_key as "assetKey",v.file_name as "fileName",a.approved_version_id as "approvedVersionId"
                  from studio_media_assets a join studio_asset_versions v on v.id=a.approved_version_id
                  where a.job_id=$1 and ($2::text is null or position(lower($2) in lower(a.asset_key||' '||v.file_name))>0) and ($3::uuid is null or a.id>$3)
                  order by a.id limit 100
                ) row
                """,jobId,string.IsNullOrWhiteSpace(term)?null:term,after);
            return Results.Content((string)q.ExecuteScalar()!,"application/json");
        }).RequireAuthorization();
        app.MapPut("/api/production/work-orders/{id:guid}/source", (Guid id, WorkOrderSource request, ClaimsPrincipal user, JobAccessService access, WorkflowStore workflow, StudioDatabase database) => Run(() =>
        {
            using var c = database.OpenConnection(); using var tx = c.BeginTransaction();
            Guid job = OrderJob(c, tx, id);
            if (!workflow.IsProductionManager(user)||!access.HasPermission(user, job, "ViewJob")) return Results.Forbid();
            using (var guard = Command(c, tx, "select status from studio_jobs where id=$1 for update", job))
                if (guard.ExecuteScalar() is "Completed" or "Cancelled" or "Delivered") throw new ArgumentException("Job is closed for production changes.");
            if (Execute(c, tx, """
                update studio_catalog_work_orders w set asset_id=a.id,source_version_id=a.approved_version_id from studio_media_assets a
                where w.id=$1 and a.id=$2 and a.job_id=w.job_id and a.approved_version_id is not null and w.source_version_id is null
                """, id, request.AssetId) != 1) throw new ArgumentException("Choose an approved image in this Job. Pinned image versions cannot be replaced.");
            Execute(c, tx, "insert into studio_operational_audit(id,job_id,action,user_name,detail,created_at_utc) values($1,$2,'WorkOrderSourcePinned',$3,$4,now())", Guid.NewGuid(), job, WorkflowStore.Name(user), $"{id}: {request.AssetId}");
            tx.Commit(); return Results.NoContent();
        })).RequireAuthorization();
        app.MapPut("/api/production/work-orders/{id:guid}/specifications",(Guid id,WorkOrderAmendment request,ClaimsPrincipal user,WorkflowStore workflow,StudioDatabase database)=>Run(()=>
        {
            if(string.IsNullOrWhiteSpace(request.Reason)||request.Specifications.Length>4000)throw new ArgumentException("Provide revised specifications and a reason.");
            using var c=database.OpenConnection();using var tx=c.BeginTransaction();Guid job=OrderJob(c,tx,id);
            if(!workflow.CanManage(user,job))return Results.Forbid();
            using(var guard=Command(c,tx,"select status from studio_jobs where id=$1 for update",job))
                if(guard.ExecuteScalar() is "Completed" or "Cancelled" or "Delivered" or "Ready for Delivery")throw new ArgumentException("The Job is closed for order amendments.");
            using(var check=Command(c,tx,"select specifications from studio_catalog_work_orders where id=$1",id))
                if((check.ExecuteScalar() is string value?value:null)!=request.ExpectedSpecifications)throw new InvalidOperationException("Specifications changed. Refresh before amending.");
            Execute(c,tx,"update studio_catalog_work_orders set specifications=$2,exception_reason='Specifications amended; manager must reconcile production steps and variations' where id=$1",id,request.Specifications);
            Execute(c,tx,"insert into studio_operational_audit(id,job_id,action,user_name,detail,created_at_utc) values($1,$2,'WorkOrderAmended',$3,$4,now())",Guid.NewGuid(),job,WorkflowStore.Name(user),JsonSerializer.Serialize(new{order=id,before=request.ExpectedSpecifications,after=request.Specifications,reason=request.Reason}));
            tx.Commit();return Results.NoContent();
        })).RequireAuthorization();
        app.MapPost("/api/production/work-orders/{id:guid}/resolve", (Guid id, WorkOrderResolution request, ClaimsPrincipal user, WorkflowStore workflow, StudioDatabase database) => Run(() =>
        {
            if (string.IsNullOrWhiteSpace(request.Reason)) throw new ArgumentException("A reconciliation note is required.");
            using var c = database.OpenConnection(); using var tx = c.BeginTransaction(); Guid job = OrderJob(c, tx, id);
            if (!workflow.IsProductionManager(user)) return Results.Forbid();
            Execute(c,tx,"""
                update studio_job_activities a set status=case when position(lower(a.step_definition->>'variation') in lower(coalesce(w.specifications,'')))>0 then 'NotStarted' else 'Cancelled' end,
                  optional_step=case when position(lower(a.step_definition->>'variation') in lower(coalesce(w.specifications,'')))>0 then coalesce((a.step_definition->>'optional')::boolean,false) else true end,
                  blocked_reason=case when position(lower(a.step_definition->>'variation') in lower(coalesce(w.specifications,'')))>0 then null else 'Variation does not apply to ordered specifications' end
                from studio_catalog_work_orders w where w.id=$1 and a.work_order_id=w.id and w.quantity>0
                  and nullif(a.step_definition->>'variation','') is not null and (a.status='NotStarted' or (a.status='Cancelled' and a.blocked_reason='Variation does not apply to ordered specifications'))
                """,id);
            Execute(c, tx, "update studio_catalog_work_orders set exception_reason=null where id=$1", id);
            Execute(c, tx, "insert into studio_operational_audit(id,job_id,action,user_name,detail,created_at_utc) values($1,$2,'WorkOrderReconciled',$3,$4,now())", Guid.NewGuid(), job, WorkflowStore.Name(user), $"{id}: {request.Reason}");
            tx.Commit(); return Results.NoContent();
        })).RequireAuthorization();
    }
    private static Guid OrderJob(Npgsql.NpgsqlConnection c, Npgsql.NpgsqlTransaction tx, Guid order)
    { using var q = Command(c, tx, "select job_id from studio_catalog_work_orders where id=$1 for update", order); return q.ExecuteScalar() is Guid job ? job : throw new KeyNotFoundException(); }
    internal static IResult Run(Func<IResult> action)
    {
        try { return action(); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    }
}
