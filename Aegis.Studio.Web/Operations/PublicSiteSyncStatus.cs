using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Operations;

// Each channel records only a completed exchange, including successful empty polls.
// The source prevents a different configured Public Site inheriting an old success.
public static class PublicSiteSyncStatus
{
    public static void EnsureSchema(StudioDatabase database)
    {
        using var c = database.OpenConnection();
        using var q = new NpgsqlCommand("create table if not exists studio_public_site_sync_status(source text not null,channel text not null,succeeded_at_utc timestamptz not null,primary key(source,channel))", c);
        q.ExecuteNonQuery();
    }

    public static string? Source(IConfiguration configuration)
        => Uri.TryCreate(configuration["PublicSite:BaseUrl"], UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority) : null;

    public static void RecordSuccess(StudioDatabase database, IConfiguration configuration, string channel)
    {
        string? source = Source(configuration);
        if (source is null) return;
        using var c = database.OpenConnection();
        using var q = new NpgsqlCommand("insert into studio_public_site_sync_status values($1,$2,now()) on conflict(source,channel) do update set succeeded_at_utc=excluded.succeeded_at_utc", c);
        q.Parameters.AddWithValue(source);
        q.Parameters.AddWithValue(channel);
        q.ExecuteNonQuery();
    }

    public static DateTimeOffset? LastSuccess(StudioDatabase database, IConfiguration configuration)
    {
        string? source = Source(configuration);
        if (source is null) return null;
        using var c = database.OpenConnection();
        using var q = new NpgsqlCommand("select max(succeeded_at_utc) from studio_public_site_sync_status where source=$1", c);
        q.Parameters.AddWithValue(source);
        return q.ExecuteScalar() is DateTime time ? new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)) : null;
    }
}
