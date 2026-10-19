using System.Security.Claims;
using System.Text.Json;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Npgsql;
using static Aegis.Studio.Web.Production.CommercialTemplateStore;

namespace Aegis.Studio.Web.Production;

// Read projection only: existing orders, item quantities and workflow evaluation remain authoritative.
public static class WorkloadOverview
{
    public static void MapWorkloadOverview(this WebApplication app)
    {
        app.MapGet("/api/production/workload", (ClaimsPrincipal user, JobAccessService access, StudioDatabase database) =>
        {
            var permissions = access.GetPermissionMap(user);
            // Production managers need the workload projection even when they do not also have ViewJob.
            var allowed = access.GetProductionJobIds(user).ToArray();
            var production = permissions.Where(p => p.Value.ManageProduction).Select(p => p.Key).ToArray();
            using var c = database.OpenConnection();
            using var tx = c.BeginTransaction(System.Data.IsolationLevel.RepeatableRead);
            using var q = new NpgsqlCommand("""
                select jsonb_build_object(
                  'jobs',coalesce((select jsonb_agg(jsonb_build_object('id',j.id,'title',j.title,'status',j.status,'workspaceId',j.workspace_id,'workspaceName',(select name from studio_workspaces where id=j.workspace_id))) from studio_jobs j where j.id=any($1)),'[]'),
                  'activities',coalesce((select jsonb_agg(jsonb_build_object(
                    'id',a.id,'jobId',a.job_id,'title',a.title,'activityType',a.activity_type,'status',a.status,
                    'assignedTo',a.assigned_to,'dueAtUtc',a.due_at_utc,'scheduledAtUtc',a.scheduled_at_utc,
                    'handoffTo',a.handoff_to,'productionArea',a.production_area,'workOrderId',a.work_order_id,'nodeId',a.workflow_node_id,
                    'blockedReason',coalesce(a.blocked_reason,w.exception_reason,case when a.job_id=any($2) then
                      (select 'QC rejected: '||min(i.qc_reason) from studio_production_items i where i.activity_id=a.id and i.qc_state='Rejected') end,case
                      when a.approved_image_required and w.source_version_id is null then 'Approved source required'
                      when exists(select 1 from studio_activity_dependencies d join studio_job_activities p on p.id=d.depends_on_id
                        where d.activity_id=a.id and p.status not in ('Completed','Cancelled')) then 'Waiting for prerequisite' end)))
                    from studio_job_activities a left join studio_catalog_work_orders w on w.id=a.work_order_id
                    where a.job_id=any($1)),'[]'),
                  'items',coalesce((select jsonb_agg(row) from (
                    select i.activity_id as "activityId",a.job_id as "jobId",
                      case when i.source_sale_line_id is null then 'Approved images/items'
                        when upper(i.product_code) like 'PRINT%' or upper(i.product_code) like 'ID-%' then 'Physical outputs'
                        else 'Other purchased outputs' end as kind,
                      sum(i.completed_quantity) filter(where i.qc_state<>'Cancelled') as completed,
                      sum(i.quantity) filter(where i.qc_state<>'Cancelled') as quantity,
                      sum(greatest(0,i.quantity-i.completed_quantity)) filter(where i.qc_state not in ('Cancelled','Passed','Rejected')) as remaining,
                      sum(i.completed_quantity) filter(where i.qc_state='Pending') as "awaitingQc",
                      sum(i.quantity) filter(where i.qc_state='Rejected') as rework,
                      sum(i.refunded_produced_quantity) as "refundedProduced",
                      count(*) filter(where i.qc_state<>'Cancelled') as "itemRecords"
                    from studio_production_items i join studio_job_activities a on a.id=i.activity_id
                    where a.job_id=any($2) group by i.activity_id,a.job_id,kind
                    union all
                    -- Posted output lines awaiting their approved source have known quantities even before item creation.
                    select pending.activity_id,pending.job_id,pending.kind,0::bigint,sum(pending.quantity),sum(pending.quantity),
                      0::bigint,0::bigint,0::bigint,0::bigint
                    from (
                      select a.id as activity_id,a.job_id,
                        case when upper(l.code) like 'PRINT%' or upper(l.code) like 'ID-%' then 'Physical outputs' else 'Other purchased outputs' end as kind,
                        greatest(0,l.quantity-coalesce((select sum(r.quantity) from studio_pos_refund_lines r where r.sale_line_id=l.id),0)) as quantity
                      from studio_pos_sale_lines l join studio_pos_sales s on s.id=l.sale_id
                        join studio_pos_line_media m on m.sale_line_id=l.id join studio_job_activities a on a.id=s.id
                      where a.job_id=any($2) and s.status=2 and s.reference_type='StudioJob' and s.reference_id=a.job_id::text
                        and m.source_version_id is null
                        and not exists(select 1 from studio_catalog_line_snapshots snap where snap.sale_line_id=l.id and snap.requires_production)
                        and not exists(select 1 from studio_production_items existing where existing.source_sale_line_id=l.id)
                    ) pending where pending.quantity>0 group by pending.activity_id,pending.job_id,pending.kind
                  ) row),'[]'),
                  'orders',coalesce((select jsonb_agg(jsonb_build_object('id',w.id,'jobId',w.job_id,'quantity',w.quantity,
                    'exceptionReason',w.exception_reason,'sourceMissing',w.source_version_id is null,
                    'activityIds',coalesce((select jsonb_agg(a.id) from studio_job_activities a where a.work_order_id=w.id),'[]')) order by w.created_at,w.id)
                    from studio_catalog_work_orders w where w.job_id=any($1)),'[]'),
                  'graphs',coalesce((select jsonb_agg(jsonb_build_object('jobId',i.job_id,'definition',i.definition,
                    'services',coalesce((select jsonb_agg(s.catalog_item_id) from (
                      select catalog_item_id from studio_job_catalog_agreements where job_id=i.job_id and workflow_selected
                      union select snap.catalog_item_id from studio_catalog_line_snapshots snap
                        join studio_pos_sale_lines l on l.id=snap.sale_line_id join studio_pos_sales sale on sale.id=l.sale_id
                        where sale.reference_type='StudioJob' and sale.reference_id=i.job_id::text and sale.status=2
                          and l.quantity>coalesce((select sum(r.quantity) from studio_pos_refund_lines r where r.sale_line_id=l.id),0)
                    ) s),'[]')))
                    from studio_job_template_instances i where i.job_id=any($1)),'[]'))::text
                """, c, tx);
            q.Parameters.AddWithValue(allowed); q.Parameters.AddWithValue(production);
            using var document = JsonDocument.Parse((string)q.ExecuteScalar()!);
            var data = document.RootElement;
            var activities = data.GetProperty("activities").EnumerateArray().ToArray();
            var evaluations = new Dictionary<Guid, Dictionary<string, GraphNodeState>>();
            // Bundle components use the same live catalog expansion as JobGraphStore.Services.
            using var bundles = new NpgsqlCommand("select id,bundle::text from studio_pos_catalog where bundle<>'[]'::jsonb", c, tx);
            var components = new Dictionary<Guid, Guid[]>();
            using (var r = bundles.ExecuteReader()) while (r.Read())
            {
                using var bundle = JsonDocument.Parse(r.GetString(1));
                components[r.GetGuid(0)] = bundle.RootElement.EnumerateArray().Select(x => x.GetProperty("catalogItemId").GetGuid()).ToArray();
            }
            var byJob = activities.ToLookup(a => a.GetProperty("jobId").GetGuid());
            foreach (var entry in data.GetProperty("graphs").EnumerateArray())
            {
                var job = entry.GetProperty("jobId").GetGuid();
                var definition = entry.GetProperty("definition").Deserialize<CommercialTemplate>(Json)!;
                var services = entry.GetProperty("services").EnumerateArray().Select(x => x.GetGuid()).ToHashSet();
                foreach (var id in services.ToArray()) if (components.TryGetValue(id, out var ids)) services.UnionWith(ids);
                var statuses = byJob[job].Where(a => a.GetProperty("nodeId").ValueKind == JsonValueKind.String)
                    .ToDictionary(a => a.GetProperty("nodeId").GetString()!, a => a.GetProperty("status").GetString()!);
                evaluations[job] = WorkflowGraphEngine.Evaluate(JobGraphStore.Graph(definition), statuses, services).Nodes.ToDictionary(n => n.Id);
            }
            var eligibility = activities.Select(a =>
            {
                GraphNodeState? state = null;
                if (a.GetProperty("nodeId").GetString() is string node && evaluations.TryGetValue(a.GetProperty("jobId").GetGuid(), out var states)) states.TryGetValue(node, out state);
                return new { activityId = a.GetProperty("id").GetGuid(), status = state?.Status, reason = state?.Reason };
            }).ToArray();
            tx.Commit();
            return Results.Ok(new { jobs = data.GetProperty("jobs").Clone(), activities = data.GetProperty("activities").Clone(), items = data.GetProperty("items").Clone(),
                orders = data.GetProperty("orders").Clone(), eligibility, productionJobIds = production, generatedAtUtc = DateTimeOffset.UtcNow,
                quantityScope = "Item quantities require ManageProduction. Catalog quantities are ordered units; partial completion is not recorded." });
        }).RequireAuthorization();
    }
}
