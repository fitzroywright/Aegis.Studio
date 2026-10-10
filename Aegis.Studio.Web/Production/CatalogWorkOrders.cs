using System.Text.Json;
using Aegis.Studio.Web.Pos;
using Aegis.Studio.Web.Persistence;
using Npgsql;
using static Aegis.Studio.Web.Production.CommercialTemplateStore;

namespace Aegis.Studio.Web.Production;

public sealed record CatalogProductionSnapshot(PosCatalogItem Product, TemplateVersion? Template, int Quantity);

public static class CatalogWorkOrders
{
    public static CatalogProductionSnapshot[] Freeze(PosCatalogItem item, StudioDatabase db)
    {
        var catalog = new PosCatalogStore(db); var templates = new CommercialTemplateStore(db);
        var products = new List<(PosCatalogItem Item, int Quantity)> { (item, 1) };
        products.AddRange((item.Bundle ?? []).Select(p => (catalog.Get(p.CatalogItemId) ?? throw new ArgumentException("Bundle component was not found."), p.Quantity)));
        return products.Select(p =>
        {
            var template = p.Item.WorkOrderTemplateId is Guid id ? templates.Get(id) : null;
            if (!p.Item.IsActive || (p.Item.RequiresProduction && (template is null || !template.Active || template.Kind != "WorkOrder")))
                throw new ArgumentException("Catalog production products require an active Work Order Template.");
            return new CatalogProductionSnapshot(p.Item, template, p.Quantity);
        }).ToArray();
    }
    public static void SnapshotLine(NpgsqlConnection c, NpgsqlTransaction tx, Guid line, PosCatalogItem item, decimal netPrice,
        string? specifications, CatalogProductionSnapshot[] frozen)
    {
        var template = frozen.First().Template;
        Execute(c, tx, """
            insert into studio_catalog_line_snapshots(sale_line_id,catalog_item_id,catalog_price,net_unit_price,tax_rate,estimated_cost,specifications,definition,
              requires_production,template_id,template_version,template_definition)
            values($1,$2,$3,$4,$5,$6,$7,$8::jsonb,$9,$10,$11,$12::jsonb) on conflict(sale_line_id) do nothing
            """, line, item.Id, item.UnitPrice, netPrice, item.TaxRate, item.EstimatedCost, specifications,
            JsonSerializer.Serialize(frozen, Json), frozen.Any(p => p.Product.RequiresProduction), template?.Id, template?.Version,
            template is null ? null : JsonSerializer.Serialize(template.Definition, Json));
    }
    public static void Sync(NpgsqlConnection c, NpgsqlTransaction tx, Guid sale, string actor)
    {
        using var q = Command(c, tx, """
            select l.id,s.reference_id::uuid,l.quantity,
              coalesce((select sum(r.quantity)::int from studio_pos_refund_lines r where r.sale_line_id=l.id),0),
              snap.definition::text,snap.specifications,m.asset_id,m.source_version_id
            from studio_pos_sales s join studio_pos_sale_lines l on l.sale_id=s.id
              join studio_catalog_line_snapshots snap on snap.sale_line_id=l.id
              left join studio_pos_line_media m on m.sale_line_id=l.id
            where s.id=$1 and s.status=2 and snap.requires_production
            """, sale);
        using var reader = q.ExecuteReader(); var lines = new List<(Guid Line, Guid Job, int Quantity, int Refunded, string Json, string? Specs, Guid? Asset, Guid? Version)>();
        while (reader.Read()) lines.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetGuid(6), reader.IsDBNull(7) ? null : reader.GetGuid(7)));
        reader.Close();
        foreach (var line in lines)
        foreach (var snapshot in JsonSerializer.Deserialize<CatalogProductionSnapshot[]>(line.Json, Json)!)
        {
            if (!snapshot.Product.RequiresProduction) continue;
            var template = snapshot.Template ?? throw new InvalidOperationException("Work Order Template snapshot is missing.");
            Guid order = Guid.NewGuid(); int original = checked(line.Quantity * snapshot.Quantity), remaining = checked((line.Quantity - line.Refunded) * snapshot.Quantity);
            using (var existing = Command(c, tx, "select id,quantity from studio_catalog_work_orders where sale_line_id=$1 and catalog_item_id=$2 for update", line.Line, snapshot.Product.Id))
            {
                using var r = existing.ExecuteReader();
                if (r.Read())
                {
                    order = r.GetGuid(0); int previous = r.GetInt32(1); r.Close();
                    if (remaining != previous)
                    {
                        Execute(c, tx, """
                            update studio_catalog_work_orders w set quantity=$2,exception_reason=case
                              when exists(select 1 from studio_job_activities a where a.work_order_id=w.id and a.status in ('InProgress','Completed'))
                              then 'Refund/amendment affects work already started; manager reconciliation required' else w.exception_reason end where id=$1
                            """, order, remaining);
                        if (remaining == 0) Execute(c, tx, "update studio_job_activities set status='Cancelled',blocked_reason='Order refunded before production',updated_at_utc=now() where work_order_id=$1 and status in ('NotStarted','Blocked')", order);
                    }
                    continue;
                }
            }
            Execute(c, tx, """
                insert into studio_catalog_work_orders(id,job_id,catalog_item_id,sale_line_id,template_id,template_version,template_definition,
                  product_name,quantity,original_quantity,specifications,asset_id,source_version_id)
                values($1,$2,$3,$4,$5,$6,$7::jsonb,$8,$9,$10,$11,$12,$13)
                """, order, line.Job, snapshot.Product.Id, line.Line, template.Id, template.Version,
                JsonSerializer.Serialize(template.Definition, Json), snapshot.Product.Name, remaining, original, line.Specs, line.Asset, line.Version);
            InstantiateSteps(c, tx, line.Job, order, template.Definition.Steps, actor);
            if (remaining == 0) Execute(c, tx, "update studio_job_activities set status='Cancelled' where work_order_id=$1", order);
            Execute(c, tx, "insert into studio_operational_audit(id,job_id,action,user_name,detail,created_at_utc) values($1,$2,'WorkOrderGenerated',$3,$4,now())", Guid.NewGuid(), line.Job, actor, $"{snapshot.Product.Name} × {remaining}, {template.Definition.Name} v{template.Version}, sale {sale}");
        }
    }
    public static void ConfirmAgreements(NpgsqlConnection c, NpgsqlTransaction tx, Guid job)
    {
        Execute(c, tx, """
            update studio_job_catalog_agreements a set confirmed_unit_price=round(p.unit_price*(1+p.tax_rate/100),2),definition=to_jsonb(p),confirmed_at=now()
            from studio_pos_catalog p where a.job_id=$1 and a.catalog_item_id=p.id and a.confirmed_at is null
            """, job);
    }
}
