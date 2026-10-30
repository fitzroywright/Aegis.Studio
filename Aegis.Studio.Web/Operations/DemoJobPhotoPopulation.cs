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
        var jobs=new List<(Guid Id,string Title)>();
        using(var query=new NpgsqlCommand("select id,title from studio_jobs j where id::text like 'de000000-%' or id::text like 'de100000-%' or exists(select 1 from studio_frontdesk_requests r where r.job_id=j.id and r.created_by='demo-seed') order by id",connection))
        using(var reader=query.ExecuteReader())while(reader.Read())jobs.Add((reader.GetGuid(0),reader.GetString(1)));
        var versions=new PrefixedFileStorage(storage,"VersionStore");
        for(int ordinal=0;ordinal<jobs.Count;ordinal++)
        {
            (Guid job,string title)=jobs[ordinal];
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
            string category=DemoDataExpansion.PhotoCategory(title);
            int target=Math.Min(40,DemoDataExpansion.PhotoCount(title));
            int firstPhoto=existing.Count;
            for(int photo=firstPhoto;photo<target;photo++)
            {
                report?.Invoke(new(96+3*ordinal/Math.Max(1,jobs.Count),"Photos for every job",ordinal,jobs.Count,$"Job {ordinal+1}/{jobs.Count}: loading {category} photograph {photo+1}/{target}."));
                string key=$"demo-{category}/{photo+1:000}";
                DemoDataExpansion.AddImage(connection,transaction,versions,job,key,Id(job,key+"/version"),1,photo,null,login,searchTerm:title);
                Guid asset=Id(job,key+"/asset");
                Execute(connection,transaction,"insert into studio_media_assets(id,job_id,gallery_id,asset_key,production_status) values($1,$2,$2,$3,'Needs Sorting') on conflict(job_id,asset_key) do nothing",asset,job,key);
                Execute(connection,transaction,"insert into studio_media_asset_subjects(asset_id,subject_id) select $1,id from studio_job_subjects where job_id=$2 and not is_system order by id offset ($3 % greatest(1,(select count(*)::int from studio_job_subjects where job_id=$2 and not is_system))) limit 1 on conflict do nothing",asset,job,photo);
                Execute(connection,transaction,"insert into studio_job_asset_collections(job_id,asset_key,collection_name) select $1,$2,coalesce((select nullif(trim(s.collection_name),'') from studio_media_asset_subjects a join studio_job_subjects s on s.id=a.subject_id where a.asset_id=$3 order by s.id limit 1),'Unclassified') on conflict do nothing",job,key,asset);
            }
            transaction.Commit();
            report?.Invoke(new(96+3*(ordinal+1)/Math.Max(1,jobs.Count),"Photos for every job",ordinal+1,jobs.Count,$"Verified unique library photographs for job {ordinal+1}/{jobs.Count}."));
        }
    }

    private static Guid Id(Guid job,string key)=>new(SHA256.HashData(Encoding.UTF8.GetBytes($"aegis-demo-photos/{job:D}/{key}")).AsSpan(0,16));
    private static void Execute(NpgsqlConnection connection,NpgsqlTransaction transaction,string sql,params object[] values)
    {
        using var command=new NpgsqlCommand(sql,connection,transaction);
        foreach(var value in values)command.Parameters.AddWithValue(value);
        command.ExecuteNonQuery();
    }
}

