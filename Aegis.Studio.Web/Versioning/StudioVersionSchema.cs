using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Versioning;

internal static class StudioVersionSchema
{
    private const string Sql = """
        create table if not exists studio_asset_versions
        (
            id uuid primary key,
            job_id uuid not null references studio_jobs(id) on delete cascade,
            asset_key text not null,
            version_number integer not null,
            file_name text not null,
            content_type text not null,
            size_bytes bigint not null,
            sha256 text not null,
            stored_path text not null unique,
            created_by text not null,
            created_at_utc timestamptz not null,
            source_version_id uuid null references studio_asset_versions(id),
            note text null,
            unique(job_id,asset_key,version_number)
        );
        create index if not exists ix_studio_asset_versions_history
            on studio_asset_versions(job_id,asset_key,version_number desc);
        """;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<StudioDatabase, Lazy<bool>> initialized = new();
    public static void EnsureCreated(StudioDatabase database)
    { _ = initialized.GetValue(database,db => new Lazy<bool>(() => { Create(db); return true; })).Value; }
    private static void Create(StudioDatabase database)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = Sql + Aegis.Studio.Web.Media.MediaDerivativeWorker.Schema;
        command.ExecuteNonQuery();
    }
}

