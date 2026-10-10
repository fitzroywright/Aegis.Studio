using Aegis.Studio.Web.Persistence;
using Npgsql;
using System.Text.Json;
using Aegis.Studio.Web.Production;

namespace Aegis.Studio.Web.Pos;

public sealed class PosCatalogStore
{
    private readonly StudioDatabase database;

    public PosCatalogStore(StudioDatabase database)
    {
        this.database = database;
    }

    public IReadOnlyList<PosCatalogItem> GetAll(bool includeInactive = false)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id,code,name,category,unit_price,is_active,sort_order,updated_at_utc,description,estimated_cost,tax_rate,options::text,requires_production,work_order_template_id,bundle::text,price_list_key from studio_pos_catalog where $1 or is_active order by sort_order,name";
        command.Parameters.AddWithValue(includeInactive);
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<PosCatalogItem> values = [];
        while (reader.Read()) values.Add(Read(reader));
        return values;
    }

    public PosCatalogItem? Get(Guid id)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id,code,name,category,unit_price,is_active,sort_order,updated_at_utc,description,estimated_cost,tax_rate,options::text,requires_production,work_order_template_id,bundle::text,price_list_key from studio_pos_catalog where id=$1";
        command.Parameters.AddWithValue(id);
        using NpgsqlDataReader reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public PosCatalogItem Save(PosCatalogItemRequest request, Guid? id = null)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw new InvalidOperationException("Item name is required.");
        if (request.EstimatedCost < 0 || request.TaxRate is < 0 or > 100) throw new InvalidOperationException("Invalid estimated cost or tax rate.");
        if (string.IsNullOrWhiteSpace(request.Code)) throw new InvalidOperationException("Unique item code is required.");
        if (request.RequiresProduction && request.WorkOrderTemplateId is null) throw new InvalidOperationException("Production products require a Work Order Template.");
        if ((request.Bundle ?? []).Any(x => x.Quantity < 1)) throw new InvalidOperationException("Bundle quantities must be positive.");
        if (request.UnitPrice < 0m) throw new InvalidOperationException("Unit price cannot be negative.");
        PosCatalogItem item = new(id ?? Guid.NewGuid(), request.Code?.Trim() ?? string.Empty, request.Name.Trim(), string.IsNullOrWhiteSpace(request.Category) ? "General" : request.Category.Trim(), request.UnitPrice, request.IsActive, request.SortOrder, DateTimeOffset.UtcNow,request.Description,request.EstimatedCost,request.TaxRate,request.Options,request.RequiresProduction,request.WorkOrderTemplateId,request.Bundle);
        using NpgsqlConnection connection = database.OpenConnection();
        using var tx=connection.BeginTransaction();
        using(var guard=new NpgsqlCommand("select pg_advisory_xact_lock(hashtextextended($1,0))",connection,tx)){guard.Parameters.AddWithValue("Catalog:"+item.Code.ToUpperInvariant());guard.ExecuteNonQuery();}
        using(var unique=new NpgsqlCommand("select exists(select 1 from studio_pos_catalog where lower(code)=lower($1) and id<>$2)",connection,tx)){unique.Parameters.AddWithValue(item.Code);unique.Parameters.AddWithValue(item.Id);if((bool)unique.ExecuteScalar()!)throw new InvalidOperationException("Item code is already in use.");}
        if(item.WorkOrderTemplateId is Guid template){using var check=new NpgsqlCommand("select exists(select 1 from studio_commercial_templates where id=$1 and kind='WorkOrder' and active)",connection,tx);check.Parameters.AddWithValue(template);if(!(bool)check.ExecuteScalar()!)throw new InvalidOperationException("Select an active Work Order Template.");}
        foreach(var part in item.Bundle??[]){using var check=new NpgsqlCommand("select exists(select 1 from studio_pos_catalog where id=$1 and is_active and bundle='[]'::jsonb)",connection,tx);check.Parameters.AddWithValue(part.CatalogItemId);if(part.CatalogItemId==item.Id||!(bool)check.ExecuteScalar()!)throw new InvalidOperationException("Bundle components must be active individual catalog items.");}
        using NpgsqlCommand command = connection.CreateCommand();command.Transaction=tx;
        command.CommandText = """
            insert into studio_pos_catalog(id,code,name,category,unit_price,is_active,sort_order,updated_at_utc,description,estimated_cost,tax_rate,options,requires_production,work_order_template_id,bundle)
            values ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12::jsonb,$13,$14,$15::jsonb)
            on conflict(id) do update set code=excluded.code,name=excluded.name,category=excluded.category,unit_price=excluded.unit_price,is_active=excluded.is_active,sort_order=excluded.sort_order,updated_at_utc=excluded.updated_at_utc,description=excluded.description,estimated_cost=excluded.estimated_cost,tax_rate=excluded.tax_rate,options=excluded.options,requires_production=excluded.requires_production,work_order_template_id=excluded.work_order_template_id,bundle=excluded.bundle
            """;
        command.Parameters.AddWithValue(item.Id); command.Parameters.AddWithValue(item.Code); command.Parameters.AddWithValue(item.Name); command.Parameters.AddWithValue(item.Category); command.Parameters.AddWithValue(item.UnitPrice); command.Parameters.AddWithValue(item.IsActive); command.Parameters.AddWithValue(item.SortOrder); command.Parameters.AddWithValue(item.UpdatedAtUtc);
        foreach(var value in new object?[]{item.Description,item.EstimatedCost,item.TaxRate,JsonSerializer.Serialize(item.Options??new()),item.RequiresProduction,item.WorkOrderTemplateId,JsonSerializer.Serialize(item.Bundle??[])})command.Parameters.AddWithValue(value??DBNull.Value);
        command.ExecuteNonQuery();tx.Commit();
        return item;
    }

    private static PosCatalogItem Read(NpgsqlDataReader r) => new(r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetDecimal(4),r.GetBoolean(5),r.GetInt32(6),r.GetFieldValue<DateTimeOffset>(7),r.GetString(8),r.GetDecimal(9),r.GetDecimal(10),JsonSerializer.Deserialize<Dictionary<string,string[]>>(r.GetString(11)),r.GetBoolean(12),r.IsDBNull(13)?null:r.GetGuid(13),JsonSerializer.Deserialize<BundleComponent[]>(r.GetString(14)),r.GetString(15));
}

public sealed record PosCatalogItem(Guid Id,string Code,string Name,string Category,decimal UnitPrice,bool IsActive,int SortOrder,DateTimeOffset UpdatedAtUtc,string Description="",decimal EstimatedCost=0,decimal TaxRate=0,Dictionary<string,string[]>? Options=null,bool RequiresProduction=false,Guid? WorkOrderTemplateId=null,BundleComponent[]? Bundle=null,string PriceListKey="Standard");
public sealed record PosCatalogItemRequest(string? Code,string Name,string? Category,decimal UnitPrice,bool IsActive=true,int SortOrder=0,string Description="",decimal EstimatedCost=0,decimal TaxRate=0,Dictionary<string,string[]>? Options=null,bool RequiresProduction=false,Guid? WorkOrderTemplateId=null,BundleComponent[]? Bundle=null);
