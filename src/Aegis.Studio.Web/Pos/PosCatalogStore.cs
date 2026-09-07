using Aegis.Studio.Web.Persistence;
using Npgsql;

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
        command.CommandText = "select id,code,name,category,unit_price,is_active,sort_order,updated_at_utc from studio_pos_catalog where $1 or is_active order by sort_order,name";
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
        command.CommandText = "select id,code,name,category,unit_price,is_active,sort_order,updated_at_utc from studio_pos_catalog where id=$1";
        command.Parameters.AddWithValue(id);
        using NpgsqlDataReader reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public PosCatalogItem Save(PosCatalogItemRequest request, Guid? id = null)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw new InvalidOperationException("Item name is required.");
        if (request.UnitPrice < 0m) throw new InvalidOperationException("Unit price cannot be negative.");
        PosCatalogItem item = new(id ?? Guid.NewGuid(), request.Code?.Trim() ?? string.Empty, request.Name.Trim(), string.IsNullOrWhiteSpace(request.Category) ? "General" : request.Category.Trim(), request.UnitPrice, request.IsActive, request.SortOrder, DateTimeOffset.UtcNow);
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            insert into studio_pos_catalog(id,code,name,category,unit_price,is_active,sort_order,updated_at_utc)
            values ($1,$2,$3,$4,$5,$6,$7,$8)
            on conflict(id) do update set code=excluded.code,name=excluded.name,category=excluded.category,unit_price=excluded.unit_price,is_active=excluded.is_active,sort_order=excluded.sort_order,updated_at_utc=excluded.updated_at_utc
            """;
        command.Parameters.AddWithValue(item.Id); command.Parameters.AddWithValue(item.Code); command.Parameters.AddWithValue(item.Name); command.Parameters.AddWithValue(item.Category); command.Parameters.AddWithValue(item.UnitPrice); command.Parameters.AddWithValue(item.IsActive); command.Parameters.AddWithValue(item.SortOrder); command.Parameters.AddWithValue(item.UpdatedAtUtc); command.ExecuteNonQuery();
        return item;
    }

    private static PosCatalogItem Read(NpgsqlDataReader r) => new(r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetDecimal(4),r.GetBoolean(5),r.GetInt32(6),r.GetFieldValue<DateTimeOffset>(7));
}

public sealed record PosCatalogItem(Guid Id,string Code,string Name,string Category,decimal UnitPrice,bool IsActive,int SortOrder,DateTimeOffset UpdatedAtUtc);
public sealed record PosCatalogItemRequest(string? Code,string Name,string? Category,decimal UnitPrice,bool IsActive=true,int SortOrder=0);
