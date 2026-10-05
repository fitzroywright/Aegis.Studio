using System.Security.Claims;
using Aegis.Studio.Web.Media;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Npgsql;
namespace Aegis.Studio.Web.Production;

public static class ProductionOrderLinker
{
    public const string Schema="""
        create table if not exists studio_pos_line_media(
          sale_line_id uuid primary key references studio_pos_sale_lines(id) on delete cascade,
          asset_id uuid null references studio_media_assets(id),source_version_id uuid null references studio_asset_versions(id));
        alter table studio_production_items add column if not exists source_sale_line_id uuid null references studio_pos_sale_lines(id);
        alter table studio_production_items add column if not exists refunded_produced_quantity integer not null default 0;
        alter table studio_production_items drop constraint if exists studio_production_items_activity_id_asset_id_product_code_key;
        create unique index if not exists ix_production_item_generic on studio_production_items(activity_id,asset_id,product_code) where source_sale_line_id is null;
        """;
    public static bool RequiresOutput(string code)=>code.StartsWith("PRINT",StringComparison.OrdinalIgnoreCase)||code.StartsWith("ID-",StringComparison.OrdinalIgnoreCase)||code.StartsWith("DIGITAL",StringComparison.OrdinalIgnoreCase);
    public static void SyncCompletedSale(StudioDatabase db,Guid sale,string actor)
    {
        using var c=db.OpenConnection();using var tx=c.BeginTransaction();Sync(c,tx,sale,actor);tx.Commit();
    }
    public static void Sync(NpgsqlConnection c,NpgsqlTransaction tx,Guid sale,string actor)
    {
        MediaProductionStore.Execute(c,tx,"""
            insert into studio_job_activities(id,job_id,activity_type,title,description,status,priority,assigned_to,blocked_reason,created_by,created_at_utc,updated_at_utc)
            select s.id,s.reference_id::uuid,'Print','Produce order '||substring(s.id::text,1,8),
              'Use the exact approved source attached to each purchased output.','Blocked','Normal',$2,'Approved source required',$2,now(),now()
            from studio_pos_sales s where s.id=$1 and s.reference_type='StudioJob' and s.status=2
              and exists(select 1 from studio_pos_sale_lines l join studio_pos_line_media m on m.sale_line_id=l.id where l.sale_id=s.id)
            on conflict(id) do nothing
            """,sale,actor);
        MediaProductionStore.Execute(c,tx,"""
            insert into studio_production_items(id,activity_id,asset_id,source_version_id,product_code,quantity,source_sale_line_id)
            select l.id,s.id,m.asset_id,m.source_version_id,l.code,
              l.quantity-coalesce((select sum(rl.quantity) from studio_pos_refund_lines rl where rl.sale_line_id=l.id),0),l.id
            from studio_pos_sales s join studio_pos_sale_lines l on l.sale_id=s.id join studio_pos_line_media m on m.sale_line_id=l.id
            where s.id=$1 and s.status=2 and m.source_version_id is not null and m.asset_id is not null
              and l.quantity>coalesce((select sum(rl.quantity) from studio_pos_refund_lines rl where rl.sale_line_id=l.id),0)
            on conflict(id) do nothing
            """,sale);
        // Refunds cancel unproduced units. Produced-unit exceptions remain visible for reconciliation.
        MediaProductionStore.Execute(c,tx,"""
            update studio_production_items i set qc_state=case when l.quantity-coalesce(r.n,0)<=0 then 'Cancelled' else i.qc_state end,
              quantity=greatest(1,l.quantity-coalesce(r.n,0)),
              refunded_produced_quantity=i.refunded_produced_quantity+greatest(0,i.completed_quantity-greatest(0,l.quantity-coalesce(r.n,0))),
              completed_quantity=least(i.completed_quantity,greatest(0,l.quantity-coalesce(r.n,0)))
            from studio_pos_sale_lines l left join lateral(select sum(quantity)::int n from studio_pos_refund_lines where sale_line_id=l.id) r on true
            where i.source_sale_line_id=l.id and l.sale_id=$1
            """,sale);
        MediaProductionStore.Execute(c,tx,"""
            update studio_job_activities a set status=case
              when exists(select 1 from studio_pos_sale_lines l join studio_pos_line_media m on m.sale_line_id=l.id where l.sale_id=$1 and m.source_version_id is null) then 'Blocked'
              when exists(select 1 from studio_production_items i where i.activity_id=$1 and i.qc_state not in ('Passed','Cancelled')) then 'NotStarted'
              else 'Completed' end,
              blocked_reason=case when exists(select 1 from studio_pos_sale_lines l join studio_pos_line_media m on m.sale_line_id=l.id where l.sale_id=$1 and m.source_version_id is null) then 'Approved source required' else null end,
              updated_at_utc=now() where a.id=$1 and (a.status='Blocked' and a.blocked_reason='Approved source required' or a.status='NotStarted')
            """,sale);
    }
    public static void MapProductionOrders(this WebApplication app)
    {
        using(var c=app.Services.GetRequiredService<StudioDatabase>().OpenConnection())using(var cmd=new NpgsqlCommand(Schema,c))cmd.ExecuteNonQuery();
        app.MapGet("/api/jobs/{jobId:guid}/production/{saleId:guid}/order-lines",(Guid jobId,Guid saleId,ClaimsPrincipal user,JobAccessService access,StudioDatabase db)=>
        {
            if(!access.HasPermission(user,jobId,"ManageProduction"))return Results.Forbid();
            using var c=db.OpenConnection();using var cmd=new NpgsqlCommand("""
                select l.id,l.description,l.quantity,m.asset_id,m.source_version_id from studio_pos_sale_lines l
                join studio_pos_sales s on s.id=l.sale_id join studio_pos_line_media m on m.sale_line_id=l.id
                where s.id=$1 and s.reference_id=$2 and s.reference_type='StudioJob' order by l.description
                """,c);cmd.Parameters.AddWithValue(saleId);cmd.Parameters.AddWithValue(jobId.ToString("D"));using var r=cmd.ExecuteReader();var rows=new List<object>();
            while(r.Read())rows.Add(new{id=r.GetGuid(0),description=r.GetString(1),quantity=r.GetInt32(2),assetId=r.IsDBNull(3)?(Guid?)null:r.GetGuid(3),sourceVersionId=r.IsDBNull(4)?(Guid?)null:r.GetGuid(4)});return Results.Ok(rows);
        }).RequireAuthorization();
        app.MapPut("/api/jobs/{jobId:guid}/production/{saleId:guid}/order-lines/{lineId:guid}/source",(Guid jobId,Guid saleId,Guid lineId,ProductionOrderSource request,ClaimsPrincipal user,JobAccessService access,StudioDatabase db)=>
        {
            if(!access.HasPermission(user,jobId,"ManageProduction"))return Results.Forbid();
            using var c=db.OpenConnection();using var tx=c.BeginTransaction();
            using(var guard=new NpgsqlCommand("select id from studio_jobs where id=$1 and status not in ('Ready for Client','Published','Completed','Cancelled') for update",c,tx))
            {guard.Parameters.AddWithValue(jobId);if(guard.ExecuteScalar() is null)return Results.Conflict(new{error="Job is closed for production changes."});}
            using var cmd=new NpgsqlCommand("""
                update studio_pos_line_media m set asset_id=a.id,source_version_id=a.approved_version_id
                from studio_media_assets a,studio_pos_sale_lines l,studio_pos_sales s
                where m.sale_line_id=l.id and l.sale_id=s.id and s.id=$1 and s.reference_type='StudioJob' and s.reference_id=$2
                  and l.id=$3 and a.id=$4 and a.job_id=$5 and a.approved_version_id is not null and m.source_version_id is null
                """,c,tx);
            foreach(var v in new object[]{saleId,jobId.ToString("D"),lineId,request.AssetId,jobId})cmd.Parameters.AddWithValue(v);
            if(cmd.ExecuteNonQuery()!=1)return Results.Conflict(new{error="Choose an approved image belonging to this job. An existing pinned source cannot be replaced."});
            string actor=user.FindFirstValue("login_name")??user.Identity?.Name??"Studio";Sync(c,tx,saleId,actor);
            MediaProductionStore.Audit(c,tx,jobId,"OrderApprovedSourceLinked",actor,$"Order {saleId}, line {lineId}, image {request.AssetId}; approved source pinned.");tx.Commit();return Results.NoContent();
        }).RequireAuthorization();
    }
}
public sealed record ProductionOrderSource(Guid AssetId);
