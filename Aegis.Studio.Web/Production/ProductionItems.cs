using System.Security.Claims;
using System.Text.Json;
using Aegis.Studio.Web.Media;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Production;

// Items belong to an existing operational activity, preserving one task and audit system.
public static class ProductionItems
{
    public const string Schema="""
        create table if not exists studio_production_items(
          id uuid primary key,activity_id uuid not null references studio_job_activities(id) on delete cascade,
          asset_id uuid not null references studio_media_assets(id),source_version_id uuid not null references studio_asset_versions(id),
          product_code text not null default 'Approved deliverable',quantity integer not null default 1 check(quantity>0),
          completed_quantity integer not null default 0 check(completed_quantity>=0),
          qc_state text not null default 'Pending',qc_reason text null,qc_by text null,qc_at timestamptz null,
          unique(activity_id,asset_id,product_code));
        create index if not exists ix_production_items_activity on studio_production_items(activity_id,id);
        """;
    public static void MapProductionItems(this WebApplication app)
    {
        using(var c=app.Services.GetRequiredService<StudioDatabase>().OpenConnection())
        using(var cmd=new NpgsqlCommand(Schema,c))cmd.ExecuteNonQuery();
        app.MapGet("/api/production/items-summary",(ClaimsPrincipal user,JobAccessService access,StudioDatabase db)=>
        {
            var jobs=access.GetPermissionMap(user).Where(p=>p.Value.ManageProduction).Select(p=>p.Key).ToArray();
            using var c=db.OpenConnection();using var q=new NpgsqlCommand("""
                select i.activity_id,coalesce(sum(i.quantity) filter(where i.qc_state<>'Cancelled'),0),sum(i.completed_quantity),
                  count(*) filter(where i.qc_state='Passed'),sum(i.refunded_produced_quantity)
                from studio_production_items i join studio_job_activities a on a.id=i.activity_id where a.job_id=any($1) group by i.activity_id
                """,c);q.Parameters.AddWithValue(jobs);using var r=q.ExecuteReader();var rows=new List<object>();while(r.Read())rows.Add(new{activityId=r.GetGuid(0),quantity=r.GetInt64(1),completed=r.GetInt64(2),passed=r.GetInt64(3),refundedProduced=r.GetInt64(4)});return Results.Ok(rows);
        }).RequireAuthorization();
        app.MapGet("/api/jobs/{jobId:guid}/production/{activityId:guid}/items",(Guid jobId,Guid activityId,Guid? after,ClaimsPrincipal user,JobAccessService access,StudioDatabase db)=>
        {
            if(!access.HasPermission(user,jobId,"ManageProduction"))return Results.Forbid();
            using var c=db.OpenConnection();using var cmd=new NpgsqlCommand("""
                select jsonb_build_object('total',count(*),'completed',coalesce(sum(completed_quantity),0),'quantity',coalesce(sum(quantity) filter(where qc_state<>'Cancelled'),0),
                  'passed',count(*) filter(where qc_state='Passed'),'rejected',count(*) filter(where qc_state='Rejected'),
                  'items',(select coalesce(jsonb_agg(row_to_json(q)),'[]'::jsonb) from
                    (select i.id,i.asset_id as "assetId",i.source_version_id as "sourceVersionId",i.product_code as "productCode",
                       i.quantity,i.completed_quantity as "completedQuantity",i.refunded_produced_quantity as "refundedProducedQuantity",i.qc_state as "qcState",i.qc_reason as "qcReason",i.qc_by as "qcBy",
                       v.file_name as "fileName",v.version_number as "versionNumber"
                     from studio_production_items i join studio_asset_versions v on v.id=i.source_version_id
                     where i.activity_id=$2 and exists(select 1 from studio_job_activities scope where scope.id=i.activity_id and scope.job_id=$1) and ($3::uuid is null or i.id>$3) order by i.id limit 100) q))::text
                from studio_production_items i join studio_job_activities a on a.id=i.activity_id where a.job_id=$1 and a.id=$2
                """,c);cmd.Parameters.AddWithValue(jobId);cmd.Parameters.AddWithValue(activityId);cmd.Parameters.AddWithValue((object?)after??DBNull.Value);
            return Results.Ok(JsonSerializer.Deserialize<JsonElement>((string)cmd.ExecuteScalar()!));
        }).RequireAuthorization();
        app.MapPut("/api/jobs/{jobId:guid}/production/{activityId:guid}/items/{itemId:guid}",
            (Guid jobId,Guid activityId,Guid itemId,ProductionItemQcRequest request,ClaimsPrincipal user,JobAccessService access,StudioDatabase db)=>
        {
            if(!access.HasPermission(user,jobId,"ManageProduction"))return Results.Forbid();
            try
            {
                Validate(request);
                string actor=user.FindFirstValue("login_name")??user.Identity?.Name??"Studio";
                using var c=db.OpenConnection();using var tx=c.BeginTransaction();
                using(var guard=new NpgsqlCommand("select id from studio_jobs where id=$1 and status in ('Active Production','Changes Requested') for update",c,tx))
                {guard.Parameters.AddWithValue(jobId);if(guard.ExecuteScalar() is null)throw new ArgumentException("This job is closed for production changes.");}
                using var update=new NpgsqlCommand("""
                    update studio_production_items i set completed_quantity=$4,qc_state=$5,qc_reason=$6,qc_by=$7,qc_at=now()
                    from studio_job_activities a where a.id=i.activity_id and a.job_id=$1 and a.id=$2 and i.id=$3
                      and i.qc_state<>'Cancelled' and $4<=i.quantity and ($5<>'Passed' or $4=i.quantity) returning i.asset_id
                    """,c,tx);
                foreach(var value in new object?[]{jobId,activityId,itemId,request.CompletedQuantity,request.State,request.Reason,actor})update.Parameters.AddWithValue(value??DBNull.Value);
                if(update.ExecuteScalar() is not Guid asset)throw new ArgumentException("Item missing or quantity does not match the production order.");
                MediaProductionStore.Execute(c,tx,"""
                    update studio_media_assets a set production_status=case
                      when exists(select 1 from studio_production_items where asset_id=a.id and qc_state='Rejected') then 'Needs Editing'
                      when exists(select 1 from studio_production_items where asset_id=a.id and qc_state not in ('Passed','Cancelled')) then 'Needs QC'
                      else 'Completed' end where a.id=$1
                    """,asset);
                MediaProductionStore.Execute(c,tx,"""
                    update studio_job_activities set status=case when exists(select 1 from studio_production_items where activity_id=$1 and qc_state='Rejected') then 'ReworkRequired' when exists(select 1 from studio_production_items where activity_id=$1 and qc_state not in ('Passed','Cancelled')) then 'InProgress' else 'Completed' end,
                      started_at_utc=coalesce(started_at_utc,now()),completed_at_utc=case when exists(select 1 from studio_production_items where activity_id=$1 and qc_state not in ('Passed','Cancelled')) then null else now() end,
                      updated_at_utc=now() where id=$1
                    """,activityId);
                MediaProductionStore.Audit(c,tx,jobId,"ProductionQC:"+request.State,actor,$"Item {itemId}; approved source retained; {request.CompletedQuantity} produced. {request.Reason}");
                tx.Commit();return Results.NoContent();
            }
            catch(ArgumentException error){return Results.BadRequest(new{error=error.Message});}
        }).RequireAuthorization();
    }
    public static Guid CreateApprovedBatch(NpgsqlConnection c,NpgsqlTransaction tx,Guid job,string actor)
    {
        using(var guard=new NpgsqlCommand("select id from studio_jobs where id=$1 for update",c,tx))
        {guard.Parameters.AddWithValue(job);if(guard.ExecuteScalar() is null)throw new ArgumentException("Job not found.");}
        MediaProductionStore.Execute(c,tx,"update studio_media_assets a set approved_version_id=(select id from studio_asset_versions where job_id=a.job_id and asset_key=a.asset_key order by version_number desc limit 1),production_status='Ready for Production' where a.job_id=$1",job);
        Guid batch=Guid.NewGuid();
        MediaProductionStore.Execute(c,tx,"""
            insert into studio_job_activities(id,job_id,activity_type,title,status,priority,created_by,created_at_utc,updated_at_utc)
            values($2,$1,'QC','Complete production and quality control','NotStarted','Normal',$3,now(),now())
            """,job,batch,actor);
        MediaProductionStore.Execute(c,tx,"""
            insert into studio_production_items(id,activity_id,asset_id,source_version_id)
            select gen_random_uuid(),$2,a.id,a.approved_version_id from studio_media_assets a
            where a.job_id=$1 and a.approved_version_id is not null
            """,job,batch);
        MediaProductionStore.Execute(c,tx,"update studio_pos_line_media m set source_version_id=a.approved_version_id from studio_media_assets a where m.asset_id=a.id and a.job_id=$1 and m.source_version_id is null",job);
        var sales=new List<Guid>();
        using(var orders=new NpgsqlCommand("select id from studio_pos_sales where reference_type='StudioJob' and reference_id=$1 and status=2",c,tx))
        {orders.Parameters.AddWithValue(job.ToString("D"));using var r=orders.ExecuteReader();while(r.Read())sales.Add(r.GetGuid(0));}
        foreach(var sale in sales)ProductionOrderLinker.Sync(c,tx,sale,actor);
        return batch;
    }

    public static void Validate(ProductionItemQcRequest request)
    {
        if(request.State is not ("Pending" or "Passed" or "Rejected"))throw new ArgumentException("Choose a supported QC result.");
        if(request.CompletedQuantity<0)throw new ArgumentException("Completed quantity cannot be negative.");
        if((request.Reason?.Length??0)>4000)throw new ArgumentException("QC notes are too long.");
        if(request.State=="Rejected"&&string.IsNullOrWhiteSpace(request.Reason))throw new ArgumentException("Record the rejection reason so production can rework the item.");
    }
}
public sealed record ProductionItemQcRequest(int CompletedQuantity,string State,string? Reason);
