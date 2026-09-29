using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Versioning;
using Npgsql;

namespace Aegis.Studio.Web.Media;

public sealed class MediaProductionStore(StudioDatabase database, StudioVersionStore versions)
{
    public static void Migrate(StudioDatabase database)
    {
        using var c = database.OpenConnection(); using var tx = c.BeginTransaction();
        string[] statements = [
            "create table if not exists studio_media_migrations(version integer primary key,applied_at timestamptz not null default now())",
            "create table if not exists studio_media_galleries(id uuid primary key references studio_jobs(id) on delete cascade,name text not null default 'Media',default_lens text not null default 'Auto')",
            "insert into studio_media_galleries(id) select id from studio_jobs on conflict do nothing",
            "alter table studio_job_subjects add column if not exists gallery_id uuid null references studio_media_galleries(id)",
            "update studio_job_subjects set gallery_id=job_id where gallery_id is null",
            "alter table studio_job_subjects add column if not exists production_status text not null default 'Not Photographed'",
            "alter table studio_job_subjects add column if not exists attributes jsonb not null default '{}'::jsonb",
            "create table if not exists studio_media_assets(id uuid primary key,job_id uuid not null references studio_jobs(id) on delete cascade,gallery_id uuid not null references studio_media_galleries(id),asset_key text not null,production_status text not null default 'Needs Sorting',assigned_to text null,capture_at_utc timestamptz null,unique(job_id,asset_key))",
            "create table if not exists studio_media_asset_subjects(asset_id uuid not null references studio_media_assets(id) on delete cascade,subject_id uuid not null references studio_job_subjects(id) on delete cascade,primary key(asset_id,subject_id))",
            "create table if not exists studio_media_edit_batches(id uuid primary key,job_id uuid not null references studio_jobs(id) on delete cascade,created_by text not null,created_at_utc timestamptz not null default now())",
            "create table if not exists studio_media_edit_items(token uuid primary key,batch_id uuid not null references studio_media_edit_batches(id) on delete cascade,asset_id uuid not null references studio_media_assets(id),source_version_id uuid not null references studio_asset_versions(id),export_name text not null,unique(batch_id,asset_id))",
            "create table if not exists studio_media_unmatched(id uuid primary key,job_id uuid not null references studio_jobs(id) on delete cascade,staging_key text not null,version_id uuid not null references studio_asset_versions(id),reason text not null,resolved_asset_id uuid null references studio_media_assets(id),created_at_utc timestamptz not null default now())",
            "create table if not exists studio_media_returns(job_id uuid not null,sha256 text not null,asset_id uuid not null references studio_media_assets(id),version_id uuid not null references studio_asset_versions(id),primary key(job_id,sha256,asset_id))",
            "alter table studio_workspaces add column if not exists icon_name text not null default 'folder'",
            "alter table studio_workspaces add column if not exists accent_color text not null default '#b99750'",
            "insert into studio_media_migrations(version) values(1) on conflict do nothing"
        ];
        foreach (string sql in statements) Execute(c, tx, sql);
        tx.Commit();
    }
    public void Sync(Guid job)
    {
        using var c = database.OpenConnection(); using var tx = c.BeginTransaction();
        Execute(c,tx,"insert into studio_media_galleries(id) select id from studio_jobs where id=$1 on conflict do nothing",job);
        Execute(c,tx,"insert into studio_media_assets(id,job_id,gallery_id,asset_key) select md5($1::text||':'||v.asset_key)::uuid,$1,$1,v.asset_key from studio_asset_versions v where v.job_id=$1 and v.asset_key not like 'unmatched/%' and not exists(select 1 from studio_media_unmatched u where u.version_id=v.id) group by v.asset_key on conflict(job_id,asset_key) do nothing",job);
        Execute(c,tx,"update studio_job_subjects set gallery_id=$1 where job_id=$1 and gallery_id is null",job);
        tx.Commit();
    }
    public JsonElement Snapshot(Guid job)
    {
        Sync(job);
        using var c=database.OpenConnection();
        using var cmd=new NpgsqlCommand("""
            select jsonb_build_object(
            'gallery',(select to_jsonb(g) from studio_media_galleries g where id=$1),
            'assets',coalesce((select jsonb_agg(jsonb_build_object('id',a.id,'assetKey',a.asset_key,'status',a.production_status,'assignedTo',a.assigned_to,'captureAtUtc',a.capture_at_utc,
              'versionId',v.id,'fileName',v.file_name,'contentType',v.content_type,'sizeBytes',v.size_bytes,'version',v.version_number,'uploadedAtUtc',v.created_at_utc,'uploadedBy',v.created_by,'photographer',(select assigned_to from studio_jobs where id=$1),'shootDate',(select event_date from studio_jobs where id=$1),
              'originalFileName',(select file_name from studio_asset_versions where job_id=$1 and asset_key=a.asset_key order by version_number limit 1),
              'collection',coalesce(col.collection_name,''),'subjects',coalesce((select jsonb_agg(subject_id) from studio_media_asset_subjects where asset_id=a.id),'[]'::jsonb)))
              from studio_media_assets a join lateral(select * from studio_asset_versions where job_id=a.job_id and asset_key=a.asset_key order by version_number desc limit 1)v on true
              left join studio_job_asset_collections col on col.job_id=a.job_id and col.asset_key=a.asset_key where a.job_id=$1),'[]'::jsonb),
            'subjects',coalesce((select jsonb_agg(jsonb_build_object('id',s.id,'name',s.name,'reference',s.reference,'notes',s.notes,'status',case when s.production_status='Not Photographed' and exists(select 1 from studio_media_asset_subjects where subject_id=s.id) then 'Photographed' else s.production_status end,'attributes',s.attributes,
              'imageCount',(select count(*) from studio_media_asset_subjects where subject_id=s.id))) from studio_job_subjects s where job_id=$1),'[]'::jsonb),
            'unmatched',coalesce((select jsonb_agg(jsonb_build_object('id',u.id,'fileName',v.file_name,'versionId',v.id,'reason',u.reason)) from studio_media_unmatched u join studio_asset_versions v on v.id=u.version_id where u.job_id=$1 and u.resolved_asset_id is null),'[]'::jsonb))::text
            """,c);
        cmd.Parameters.AddWithValue(job);return JsonSerializer.Deserialize<JsonElement>((string)cmd.ExecuteScalar()!);
    }
    public void UpdateAsset(Guid job,Guid id,MediaAssetUpdate request,string user)
    {
        MediaProductionRules.ValidateState(request.Status);
        if((request.Collection?.Length??0)>100||(request.AssignedTo?.Length??0)>200||(request.Subjects?.Length??0)>500)throw new ArgumentException("Asset details are too long.");
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        string key=AssetKey(c,tx,job,id);
        foreach(Guid subject in request.Subjects??[])
            if(Scalar(c,tx,"select id from studio_job_subjects where id=$1 and job_id=$2",subject,job) is null)throw new ArgumentException("Subject belongs to another job or no longer exists.");
        Execute(c,tx,"update studio_media_assets set production_status=$2,assigned_to=$3,capture_at_utc=$4 where id=$1",id,request.Status,(object?)request.AssignedTo??DBNull.Value,(object?)request.CaptureAtUtc?.ToUniversalTime()??DBNull.Value);
        Execute(c,tx,"delete from studio_media_asset_subjects where asset_id=$1",id);
        foreach(Guid subject in (request.Subjects??[]).Distinct())Execute(c,tx,"insert into studio_media_asset_subjects values($1,$2)",id,subject);
        if(string.IsNullOrWhiteSpace(request.Collection))Execute(c,tx,"delete from studio_job_asset_collections where job_id=$1 and asset_key=$2",job,key);
        else Execute(c,tx,"insert into studio_job_asset_collections values($1,$2,$3) on conflict(job_id,asset_key) do update set collection_name=excluded.collection_name",job,key,request.Collection.Trim());
        Audit(c,tx,job,"MediaChanged",user,"Updated image subjects, collection and production state.");tx.Commit();
    }
    public int ImportSubjects(Guid job,RosterImport request,string user)
    {
        var rows=MediaProductionRules.ReadRoster(request.Csv);var headers=rows[0];
        int name=Array.FindIndex(headers,x=>x.Equals(request.NameColumn,StringComparison.OrdinalIgnoreCase));
        int identifier=Array.FindIndex(headers,x=>x.Equals(request.IdColumn,StringComparison.OrdinalIgnoreCase));
        if(name<0||identifier<0)throw new ArgumentException("Select valid Name and Subject ID columns.");
        var data=rows.Skip(1).Select(row=>(Name:Cell(row,name),Id:Cell(row,identifier),Row:row)).ToList();
        if(data.Any(x=>string.IsNullOrWhiteSpace(x.Name)||x.Name.Length>200||string.IsNullOrWhiteSpace(x.Id)||x.Id.Length>200)||data.Select(x=>x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=data.Count)
            throw new ArgumentException("Every row needs a name and a unique Subject ID (maximum 200 characters).");
        Sync(job);using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        Execute(c,tx,"select pg_advisory_xact_lock(hashtextextended($1,0))","roster:"+job);
        foreach(var row in data)
        {
            using var lookup=new NpgsqlCommand("select id from studio_job_subjects where job_id=$1 and lower(reference)=lower($2)",c,tx);lookup.Parameters.AddWithValue(job);lookup.Parameters.AddWithValue(row.Id);
            var ids=new List<Guid>();using(var reader=lookup.ExecuteReader()){while(reader.Read())ids.Add(reader.GetGuid(0));}
            if(ids.Count>1)throw new ArgumentException("Existing Subject ID is ambiguous: "+row.Id);
            Guid id=ids.Count==1?ids[0]:Guid.NewGuid();
            var attributes=headers.Select((h,i)=>(h,v:Cell(row.Row,i))).Where(x=>!string.IsNullOrWhiteSpace(x.h)).GroupBy(x=>x.h).ToDictionary(x=>x.Key,x=>x.First().v);
            Execute(c,tx,"insert into studio_job_subjects(id,job_id,gallery_id,name,reference,notes,updated_by,attributes) values($1,$2,$2,$3,$4,'',$5,$6::jsonb) on conflict(id) do update set name=excluded.name,attributes=excluded.attributes,updated_by=excluded.updated_by,updated_at_utc=now()",id,job,row.Name,row.Id,user,JsonSerializer.Serialize(attributes));
        }
        Audit(c,tx,job,"RosterImported",user,$"Imported or updated {data.Count} subjects.");tx.Commit();return data.Count;
    }
    public void SubjectStatus(Guid job,Guid subject,string status,string user)
    {
        if(status!="Not Photographed")MediaProductionRules.ValidateState(status);
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        if(Execute(c,tx,"update studio_job_subjects set production_status=$3,updated_by=$4,updated_at_utc=now() where id=$1 and job_id=$2",subject,job,status,user)!=1)throw new KeyNotFoundException();
        Audit(c,tx,job,"SubjectStatusChanged",user,"Subject marked "+status);tx.Commit();
    }
    public byte[] Export(Guid job,Guid[] ids,string user)
    {
        if(ids.Length==0||ids.Length>200)throw new ArgumentException("Select 1–200 images per editing batch.");
        Sync(job);using var c=database.OpenConnection();using var tx=c.BeginTransaction();Guid batch=Guid.NewGuid();
        Execute(c,tx,"insert into studio_media_edit_batches(id,job_id,created_by) values($1,$2,$3)",batch,job,user);
        using var output=new MemoryStream();var manifest=new List<object>();long total=0;
        using(var zip=new ZipArchive(output,ZipArchiveMode.Create,true))foreach(Guid id in ids.Distinct())
        {
            string key=AssetKey(c,tx,job,id);var version=versions.GetWithinTransaction(c,tx,job,key);total+=version.SizeBytes;
            if(total>500_000_000)throw new ArgumentException("Batch is larger than 500 MB. Select fewer images.");
            Guid token=Guid.NewGuid();string name=id.ToString("N")+Path.GetExtension(version.FileName);
            Execute(c,tx,"insert into studio_media_edit_items values($1,$2,$3,$4,$5)",token,batch,id,version.Id,name);
            using var input=versions.OpenStored(version);using var bytes=new MemoryStream();input.CopyTo(bytes);
            byte[] file=MediaProductionRules.StampJpeg(bytes.ToArray(),token);
            using(var entry=zip.CreateEntry(name,CompressionLevel.NoCompression).Open())entry.Write(file);
            manifest.Add(new{assetId=id,jobId=job,galleryId=job,batchId=batch,exportToken=token,sourceVersionId=version.Id,exportedFile=name,originalFileName=version.FileName});
        }
        // Reopen in update mode only after the original archive central directory is written.
        output.Position=0;using(var zip=new ZipArchive(output,ZipArchiveMode.Update,true))using(var writer=new StreamWriter(zip.CreateEntry("studio-editing-manifest.json").Open()))writer.Write(JsonSerializer.Serialize(manifest));
        Audit(c,tx,job,"EditingBatchExported",user,$"Exported {manifest.Count} images for editing.");tx.Commit();return output.ToArray();
    }
    public object Ingest(Guid job,IFormFile file,bool returned,Guid? explicitAsset,string user,string? note)
    {
        if(file.Length<=0||file.Length>100_000_000)throw new ArgumentException("Files must be between 1 byte and 100 MB.");
        Sync(job);using var memory=new MemoryStream();using(var source=file.OpenReadStream())source.CopyTo(memory);
        byte[] bytes=memory.ToArray();string hash=Convert.ToHexString(SHA256.HashData(bytes));Guid? asset=explicitAsset;Guid? sourceVersion=null;
        using(var c=database.OpenConnection())
        {
            Guid? token=MediaProductionRules.ReadExportToken(bytes);
            returned=returned||token.HasValue;
            if(returned&&token.HasValue&&asset is null)
            {using var cmd=new NpgsqlCommand("select i.asset_id,i.source_version_id from studio_media_edit_items i join studio_media_edit_batches b on b.id=i.batch_id where i.token=$1 and b.job_id=$2",c);cmd.Parameters.AddWithValue(token.Value);cmd.Parameters.AddWithValue(job);using var r=cmd.ExecuteReader();if(r.Read()){asset=r.GetGuid(0);sourceVersion=r.GetGuid(1);}}
        }
        if(returned&&asset is null)
        {
            using var c=database.OpenConnection();using var tx=c.BeginTransaction();Guid id=Guid.NewGuid();
            string key="unmatched/"+Guid.NewGuid().ToString("N");memory.Position=0;var version=versions.Add(c,tx,job,key,file.FileName,file.ContentType,memory,user,note);
            Execute(c,tx,"insert into studio_media_unmatched(id,job_id,staging_key,version_id,reason) values($1,$2,$3,$4,'No unique retained export identity. Select the original image.')",id,job,key,version.Id);
            Audit(c,tx,job,"UnmatchedUpload",user,"Returned file requires manual matching.");tx.Commit();return new{unmatched=true,id};
        }
        using(var c=database.OpenConnection())using(var tx=c.BeginTransaction())
        {
            string key=asset.HasValue?AssetKey(c,tx,job,asset.Value):Guid.NewGuid().ToString("D");
            Execute(c,tx,"select pg_advisory_xact_lock(hashtextextended($1,0))",job.ToString("D")+":"+key);
            if(asset.HasValue&&Scalar(c,tx,"select version_id from studio_media_returns where job_id=$1 and sha256=$2 and asset_id=$3",job,hash,asset.Value) is Guid prior)return new{unmatched=false,id=prior};
            if(sourceVersion.HasValue && Scalar(c,tx,"select id from studio_asset_versions where job_id=$1 and asset_key=$2 order by version_number desc limit 1",job,key) is Guid latest && latest != sourceVersion.Value)
            {
                Guid unmatched=Guid.NewGuid();string staging="unmatched/"+unmatched.ToString("N");memory.Position=0;
                var staged=versions.Add(c,tx,job,staging,file.FileName,file.ContentType,memory,user,note,sourceVersion);
                Execute(c,tx,"insert into studio_media_unmatched(id,job_id,staging_key,version_id,reason) values($1,$2,$3,$4,'The editing batch is older than the current version. Review and explicitly match to promote this return.')",unmatched,job,staging,staged.Id);
                Audit(c,tx,job,"StaleEditingReturn",user,"Older editing batch requires explicit matching.");tx.Commit();return new{unmatched=true,id=unmatched};
            }
            // Existing immutable store serializes version counters; never overwrite its files.
            memory.Position=0;var version=versions.Add(c,tx,job,key,file.FileName,file.ContentType,memory,user,note,sourceVersion);
            Guid mediaId=asset??Guid.NewGuid();
            Execute(c,tx,"insert into studio_media_assets(id,job_id,gallery_id,asset_key) values($1,$2,$2,$3) on conflict(job_id,asset_key) do nothing",mediaId,job,key);
            if(asset.HasValue){Execute(c,tx,"insert into studio_media_returns values($1,$2,$3,$4)",job,hash,asset.Value,version.Id);Execute(c,tx,"update studio_media_assets set production_status='Needs Review' where id=$1",asset.Value);}
            Audit(c,tx,job,asset.HasValue?"EditedVersionReturned":"MediaUploaded",user,Path.GetFileName(file.FileName));tx.Commit();return new{unmatched=false,id=version.Id};
        }
    }
    public void Resolve(Guid job,Guid unmatched,Guid asset,string user)
    {
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        string key=AssetKey(c,tx,job,asset);
        using var cmd=new NpgsqlCommand("select version_id from studio_media_unmatched where id=$1 and job_id=$2 and resolved_asset_id is null for update",c,tx);cmd.Parameters.AddWithValue(unmatched);cmd.Parameters.AddWithValue(job);
        if(cmd.ExecuteScalar() is not Guid versionId)throw new ArgumentException("Upload is already matched or missing.");
        var original=versions.GetWithinTransaction(c,tx,job,versionId:versionId);using var stream=versions.OpenStored(original);versions.Add(c,tx,job,key,original.FileName,original.ContentType,stream,user,"Manually matched returned image",versionId);
        Execute(c,tx,"update studio_media_unmatched set resolved_asset_id=$2 where id=$1",unmatched,asset);Execute(c,tx,"update studio_media_assets set production_status='Needs Review' where id=$1",asset);
        Audit(c,tx,job,"ReturnedImageMatched",user,"Unmatched upload associated with original asset.");tx.Commit();
    }
    private static string Cell(string[] row,int index)=>index<row.Length?row[index]:"";
    private static string AssetKey(NpgsqlConnection c,NpgsqlTransaction tx,Guid job,Guid asset)=>Scalar(c,tx,"select asset_key from studio_media_assets where id=$1 and job_id=$2",asset,job) as string??throw new ArgumentException("Image is missing or belongs to another job.");
    internal static void Audit(NpgsqlConnection c,NpgsqlTransaction tx,Guid job,string action,string user,string detail)=>Execute(c,tx,"insert into studio_operational_audit(id,job_id,action,user_name,detail,created_at_utc) values($1,$2,$3,$4,$5,now())",Guid.NewGuid(),job,action,user,detail);
    internal static int Execute(NpgsqlConnection c,NpgsqlTransaction tx,string sql,params object[] args){using var cmd=new NpgsqlCommand(sql,c,tx);foreach(var value in args)cmd.Parameters.AddWithValue(value);return cmd.ExecuteNonQuery();}
    internal static object? Scalar(NpgsqlConnection c,NpgsqlTransaction tx,string sql,params object[] args){using var cmd=new NpgsqlCommand(sql,c,tx);foreach(var value in args)cmd.Parameters.AddWithValue(value);return cmd.ExecuteScalar();}
}
public sealed record MediaAssetUpdate(string Status,string? Collection,Guid[]? Subjects,string? AssignedTo,DateTimeOffset? CaptureAtUtc=null);
public sealed record RosterImport(string Csv,string NameColumn,string IdColumn);
public sealed record SubjectStateRequest(string Status);
public sealed record MediaExportRequest(Guid[] Assets);
public sealed record MatchUploadRequest(Guid AssetId);
