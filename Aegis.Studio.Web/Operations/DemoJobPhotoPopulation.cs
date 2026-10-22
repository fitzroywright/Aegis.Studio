using System.Security.Cryptography;
using System.Text;
using Common.Storage;
using Npgsql;
using Aegis.Studio.Web.Persistence;

namespace Aegis.Studio.Web.Operations;

// Top up both historical and dense scheduling jobs without touching real jobs.
internal static class DemoJobPhotoPopulation
{
    internal static void Seed(StudioDatabase database,IFileStorage storage,string login,Action<DemoSeedProgress>? report=null)
    {
        using var connection=database.OpenConnection();
        var jobs=new List<Guid>();
        using(var query=new NpgsqlCommand("select id from studio_jobs where id::text like 'de000000-%' or id::text like 'de100000-%' order by id",connection))
        using(var reader=query.ExecuteReader())while(reader.Read())jobs.Add(reader.GetGuid(0));
        var versions=new PrefixedFileStorage(storage,"VersionStore");
        for(int ordinal=0;ordinal<jobs.Count;ordinal++)
        {
            Guid job=jobs[ordinal];
            // Each job is durable independently, so interrupted downloads can resume.
            using var transaction=connection.BeginTransaction();
            Execute(connection,transaction,"select set_config('studio.demo_seed','true',true); select pg_advisory_xact_lock(72709128)");
            Execute(connection,transaction,"insert into studio_media_galleries(id) values($1) on conflict do nothing",job);
            var existing=new List<string>();
            using(var query=new NpgsqlCommand("select asset_key from studio_media_assets where job_id=$1 order by asset_key",connection,transaction))
            {
                query.Parameters.AddWithValue(job);
                using var reader=query.ExecuteReader();while(reader.Read())existing.Add(reader.GetString(0));
            }
            // Check our stored photographs on every run, including jobs already full.
            int target=existing.Count(x=>x.StartsWith("demo-people/",StringComparison.Ordinal)) + Math.Max(0,40-existing.Count);
            for(int photo=0;photo<target;photo++)
            {
                report?.Invoke(new(96+3*ordinal/Math.Max(1,jobs.Count),"Photos for every job",ordinal,jobs.Count,$"Job {ordinal+1}/{jobs.Count}: loading people photograph {photo+1}/{target}."));
                string key=$"demo-people/{photo+1:000}";
                DemoDataExpansion.AddImage(connection,transaction,versions,job,key,Id(job,key+"/version"),1,photo,null,login,searchTerm:"portrait");
                Guid asset=Id(job,key+"/asset");
                Execute(connection,transaction,"insert into studio_media_assets(id,job_id,gallery_id,asset_key,production_status) values($1,$2,$2,$3,'Needs Sorting') on conflict(job_id,asset_key) do nothing",asset,job,key);
                Execute(connection,transaction,"insert into studio_job_asset_collections(job_id,asset_key,collection_name) values($1,$2,'Unclassified') on conflict do nothing",job,key);
                Execute(connection,transaction,"insert into studio_media_asset_subjects(asset_id,subject_id) select $1,id from studio_job_subjects where job_id=$2 and not is_system order by id limit 1 on conflict do nothing",asset,job);
            }
            transaction.Commit();
            report?.Invoke(new(96+3*(ordinal+1)/Math.Max(1,jobs.Count),"Photos for every job",ordinal+1,jobs.Count,$"Verified at least 40 photographs for job {ordinal+1}/{jobs.Count}."));
        }
    }

    private static Guid Id(Guid job,string key)=>new(SHA256.HashData(Encoding.UTF8.GetBytes($"aegis-demo-people/{job:D}/{key}")).AsSpan(0,16));
    private static void Execute(NpgsqlConnection connection,NpgsqlTransaction transaction,string sql,params object[] values)
    {
        using var command=new NpgsqlCommand(sql,connection,transaction);
        foreach(var value in values)command.Parameters.AddWithValue(value);
        command.ExecuteNonQuery();
    }
}

