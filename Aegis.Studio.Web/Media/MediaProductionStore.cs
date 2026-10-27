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
            Aegis.Studio.Web.Operations.JobWorkspaceParticipation.Schema,
            MediaUploadReceipts.Schema,
            "create table if not exists studio_media_migrations(version integer primary key,applied_at timestamptz not null default now())",
            "create table if not exists studio_media_galleries(id uuid primary key references studio_jobs(id) on delete cascade,name text not null default 'Media',default_lens text not null default 'Auto')",
            "alter table studio_media_galleries add column if not exists default_collection text null",
            "create table if not exists studio_job_asset_collections(job_id uuid not null references studio_jobs(id) on delete cascade,asset_key text not null,collection_name text not null,primary key(job_id,asset_key))",
            "create table if not exists studio_media_collections(job_id uuid not null references studio_jobs(id) on delete cascade,name text not null,primary key(job_id,name))",
            "alter table studio_job_subjects add column if not exists gallery_id uuid null references studio_media_galleries(id)",
            "alter table studio_job_subjects add column if not exists production_status text not null default 'Not Photographed'",
            "alter table studio_job_subjects add column if not exists attributes jsonb not null default '{}'::jsonb",
            "alter table studio_job_subjects add column if not exists collection_name text not null default 'General'",
            "alter table studio_job_subjects add column if not exists scheduled_at_utc timestamptz null",
            "alter table studio_job_subjects add column if not exists schedule_location text not null default ''",
            "alter table studio_job_subjects add column if not exists contact_name text not null default ''",
            "alter table studio_job_subjects add column if not exists contact_email text not null default ''",
            "alter table studio_job_subjects add column if not exists contact_phone text not null default ''",
            "alter table studio_job_subjects add column if not exists photo_notes text not null default ''",
            "alter table studio_job_subjects add column if not exists is_system boolean not null default false",
            "create table if not exists studio_media_assets(id uuid primary key,job_id uuid not null references studio_jobs(id) on delete cascade,gallery_id uuid not null references studio_media_galleries(id),asset_key text not null,production_status text not null default 'Needs Sorting',assigned_to text null,capture_at_utc timestamptz null,unique(job_id,asset_key))",
            "alter table studio_media_assets add column if not exists approved_version_id uuid null references studio_asset_versions(id)",
            "alter table studio_media_assets add column if not exists tags text[] not null default '{}'",
            "alter table studio_media_assets add column if not exists deleted_at_utc timestamptz null",
            "alter table studio_media_assets add column if not exists deleted_by text null",
            "create table if not exists studio_media_asset_subjects(asset_id uuid not null references studio_media_assets(id) on delete cascade,subject_id uuid not null references studio_job_subjects(id) on delete cascade,primary key(asset_id,subject_id))",
            "create table if not exists studio_media_edit_batches(id uuid primary key,job_id uuid not null references studio_jobs(id) on delete cascade,created_by text not null,created_at_utc timestamptz not null default now())",
            "create table if not exists studio_media_edit_items(token uuid primary key,batch_id uuid not null references studio_media_edit_batches(id) on delete cascade,asset_id uuid not null references studio_media_assets(id),source_version_id uuid not null references studio_asset_versions(id),export_name text not null,unique(batch_id,asset_id))",
            "create table if not exists studio_media_unmatched(id uuid primary key,job_id uuid not null references studio_jobs(id) on delete cascade,staging_key text not null,version_id uuid not null references studio_asset_versions(id),reason text not null,resolved_asset_id uuid null references studio_media_assets(id),created_at_utc timestamptz not null default now())",
            "create table if not exists studio_media_returns(job_id uuid not null,sha256 text not null,asset_id uuid not null references studio_media_assets(id),version_id uuid not null references studio_asset_versions(id),primary key(job_id,sha256,asset_id))",
            "alter table studio_workspaces add column if not exists icon_name text not null default 'folder'",
            "alter table studio_workspaces add column if not exists accent_color text not null default '#b99750'",
            "create index if not exists ix_media_assets_job_page on studio_media_assets(job_id,id)",
            "create index if not exists ix_media_subject_assets on studio_media_asset_subjects(subject_id,asset_id)",
            "create index if not exists ix_media_subjects_job on studio_job_subjects(job_id,id)",
            "insert into studio_media_migrations(version) values(1) on conflict do nothing"
        ];
        foreach (string sql in statements) Execute(c, tx, sql);
        Execute(c,tx,Aegis.Studio.Web.WorkDay.WorkDayFieldReports.Schema);
        tx.Commit();
    }
    public void Sync(Guid job)
    {
        using var c = database.OpenConnection(); using var tx = c.BeginTransaction();
        Execute(c,tx,"insert into studio_media_galleries(id) select id from studio_jobs where id=$1 on conflict do nothing",job);
        Execute(c,tx,"insert into studio_media_collections(job_id,name) select $1,'General' where not exists(select 1 from studio_media_collections where job_id=$1) on conflict do nothing",job);
        EnsureCollection(c,tx,job,"Unclassified");
        Execute(c,tx,"update studio_job_subjects set collection_name='Unclassified' where job_id=$1 and is_system and lower(name)='unclassified'",job);
        Execute(c,tx,"insert into studio_media_assets(id,job_id,gallery_id,asset_key) select md5($1::text||':'||v.asset_key)::uuid,$1,$1,v.asset_key from studio_asset_versions v where v.job_id=$1 and v.asset_key not like 'unmatched/%' and not exists(select 1 from studio_media_unmatched u where u.version_id=v.id) group by v.asset_key on conflict(job_id,asset_key) do nothing",job);
        Execute(c,tx,"update studio_job_subjects set gallery_id=$1 where job_id=$1 and gallery_id is null",job);
        Guid unclassified = EnsureUnclassifiedSubject(c,tx,job);
        Execute(c,tx,"insert into studio_media_asset_subjects(asset_id,subject_id) select a.id,$2 from studio_media_assets a where a.job_id=$1 and not exists(select 1 from studio_media_asset_subjects x where x.asset_id=a.id) on conflict do nothing",job,unclassified);
        tx.Commit();
    }
    public JsonElement Snapshot(Guid job, MediaPageQuery? page = null)
    {
        page ??= new();
        page.Validate();
        using var c=database.OpenConnection();
        using var cmd=new NpgsqlCommand("""
            with matching as (select a.* from studio_media_assets a
              left join studio_job_asset_collections col on col.job_id=a.job_id and col.asset_key=a.asset_key
              where a.job_id=$1 and a.deleted_at_utc is null and ($2::text is null or coalesce(col.collection_name,'Unclassified')=$2)
              and ($3::uuid is null or exists(select 1 from studio_media_asset_subjects x where x.asset_id=a.id and x.subject_id=$3))
              and ($4::text is null or a.production_status=$4) and ($8::uuid is null or a.id=$8)),
            page as (select * from matching where ($5::uuid is null or id>$5) order by id limit $6)
            select jsonb_build_object(
            'totalAssets',(select count(*) from matching),
            'totalSubjects',(select count(*) from studio_job_subjects where job_id=$1 and not is_system),
            'rosterCounts',coalesce((select jsonb_object_agg(status,n) from (select status,count(*) as n from studio_workday_roster_progress where job_id=$1 group by status) x),'{}'::jsonb),
            'collectionCounts',coalesce((select jsonb_object_agg(collection_name,asset_count) from
              (select coalesce(col.collection_name,'Unclassified') as collection_name,count(*) as asset_count from studio_media_assets a
               left join studio_job_asset_collections col on col.job_id=a.job_id and col.asset_key=a.asset_key
               where a.job_id=$1 group by coalesce(col.collection_name,'Unclassified')) q),'{}'::jsonb),
            'hasMore',exists(select 1 from matching where id>(select max(id::text)::uuid from page)),
            'gallery',(select to_jsonb(g) from studio_media_galleries g where id=$1),
            'collections',coalesce((select jsonb_agg(name order by name) from studio_media_collections where job_id=$1),'[]'::jsonb),
            'assets',coalesce((select jsonb_agg(jsonb_build_object('id',a.id,'assetKey',a.asset_key,'status',a.production_status,'assignedTo',a.assigned_to,'captureAtUtc',a.capture_at_utc,'tags',a.tags,
              'approvedVersionId',a.approved_version_id,'approvedVersionNumber',(select version_number from studio_asset_versions where id=a.approved_version_id),'approvedFileName',(select file_name from studio_asset_versions where id=a.approved_version_id),'previewStatus',coalesce(d.status,'Pending'),'versionId',v.id,'fileName',v.file_name,'contentType',v.content_type,'sizeBytes',v.size_bytes,'version',v.version_number,'uploadedAtUtc',v.created_at_utc,'uploadedBy',v.created_by,'photographer',(select assigned_to from studio_jobs where id=$1),'shootDate',(select event_date from studio_jobs where id=$1),
              'originalFileName',(select file_name from studio_asset_versions where job_id=$1 and asset_key=a.asset_key order by version_number limit 1),
              'collection',coalesce(col.collection_name,''),'subjects',coalesce((select jsonb_agg(subject_id) from studio_media_asset_subjects where asset_id=a.id),'[]'::jsonb)) order by a.id)
              from page a join lateral(select * from studio_asset_versions where job_id=a.job_id and asset_key=a.asset_key order by version_number desc limit 1)v on true
              left join studio_media_derivatives d on d.version_id=v.id
              left join studio_job_asset_collections col on col.job_id=a.job_id and col.asset_key=a.asset_key),'[]'::jsonb),
            'subjects',coalesce((select jsonb_agg(jsonb_build_object('id',s.id,'name',s.name,'reference',s.reference,'notes',s.notes,'collection',s.collection_name,'scheduledAtUtc',s.scheduled_at_utc,'scheduleLocation',s.schedule_location,'contactName',s.contact_name,'contactEmail',s.contact_email,'contactPhone',s.contact_phone,'photoNotes',s.photo_notes,'isSystem',s.is_system,'status',coalesce((select p.status from studio_workday_roster_progress p where p.id=s.id),case when s.production_status='Not Photographed' and exists(select 1 from studio_media_asset_subjects where subject_id=s.id) then 'Photographed' else s.production_status end),'attributes',s.attributes,
              'imageCount',(select count(*) from studio_media_asset_subjects where subject_id=s.id))) from (select * from studio_job_subjects where job_id=$1 and ($7::text is null or name ilike '%'||$7||'%' or reference ilike '%'||$7||'%') order by is_system desc,name,id limit 500) s),'[]'::jsonb),
            'unmatched',coalesce((select jsonb_agg(jsonb_build_object('id',u.id,'fileName',v.file_name,'versionId',v.id,'reason',u.reason)) from studio_media_unmatched u join studio_asset_versions v on v.id=u.version_id where u.job_id=$1 and u.resolved_asset_id is null),'[]'::jsonb))::text
            """,c);
        foreach(var value in new object?[]{job,page.Collection,page.SubjectId,page.Status,page.After,page.Limit,page.SubjectSearch,page.AssetId})cmd.Parameters.AddWithValue(value??DBNull.Value);
        return JsonSerializer.Deserialize<JsonElement>((string)cmd.ExecuteScalar()!);
    }
    public void SoftDeleteAsset(Guid job,Guid id,string user,bool restore=false)
    {
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        if(Scalar(c,tx,"select id from studio_jobs where id=$1 and status not in ('Completed','Cancelled') for update",job) is null)
            throw new InvalidOperationException("Reopen the Job before changing media.");
        using var cmd=new NpgsqlCommand(restore?
            "update studio_media_assets set deleted_at_utc=null,deleted_by=null where id=$1 and job_id=$2 and deleted_at_utc is not null":
            "update studio_media_assets set deleted_at_utc=now(),deleted_by=$3 where id=$1 and job_id=$2 and deleted_at_utc is null",c,tx);
        cmd.Parameters.AddWithValue(id);cmd.Parameters.AddWithValue(job);
        if(!restore)cmd.Parameters.AddWithValue(user);
        if(cmd.ExecuteNonQuery()!=1)throw new KeyNotFoundException("Media asset not found or already in the requested state.");
        Audit(c,tx,job,restore?"MediaPhotoRestored":"MediaPhotoSoftDeleted",user,$"Asset {id}");
        tx.Commit();
    }
    public void UpdateAsset(Guid job,Guid id,MediaAssetUpdate request,string user,bool canDispatch=false)
    {
        MediaProductionRules.ValidateState(request.Status);
        if((request.Collection?.Length??0)>100||(request.AssignedTo?.Length??0)>200||(request.Subjects?.Length??0)>500)throw new ArgumentException("Asset details are too long.");
        string[]? tags=request.Tags is null?null:NormalizeTags(request.Tags);
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        if(Scalar(c,tx,"select id from studio_jobs where id=$1 and status not in ('Completed','Cancelled') for update",job) is null)throw new ArgumentException("Reopen the Job before changing media.");
        if(Scalar(c,tx,"select deleted_at_utc from studio_media_assets where id=$1 and job_id=$2",id,job) is DateTime)throw new InvalidOperationException("Restore the deleted photo before editing it.");
        string key=AssetKey(c,tx,job,id);
        string? previousAssignee=Scalar(c,tx,"select assigned_to from studio_media_assets where id=$1 and job_id=$2 for update",id,job) as string;
        if(!canDispatch&&!string.Equals(previousAssignee?.Trim(),request.AssignedTo?.Trim(),StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Only a Production Manager may change the production assignment.");
        foreach(Guid subject in request.Subjects??[])
            if(Scalar(c,tx,"select id from studio_job_subjects where id=$1 and job_id=$2",subject,job) is null)throw new ArgumentException("Subject belongs to another job or no longer exists.");
        if((request.Subjects?.Length??0)>0 && string.IsNullOrWhiteSpace(request.Collection))throw new ArgumentException("Classified images must belong to a collection.");
        Execute(c,tx,"update studio_media_assets set production_status=$2,assigned_to=$3,capture_at_utc=$4 where id=$1",id,request.Status,(object?)request.AssignedTo??DBNull.Value,(object?)request.CaptureAtUtc?.ToUniversalTime()??DBNull.Value);
        if(tags is not null)Execute(c,tx,"update studio_media_assets set tags=$2 where id=$1",id,tags);
        Execute(c,tx,"delete from studio_media_asset_subjects where asset_id=$1",id);
        Guid[] requestedSubjects=(request.Subjects??[]).Distinct().ToArray();
        if(requestedSubjects.Length==0)
            requestedSubjects=[EnsureUnclassifiedSubject(c,tx,job)];
        else
        {
            Guid unclassified=EnsureUnclassifiedSubject(c,tx,job);
            if(requestedSubjects.Any(x=>x!=unclassified))requestedSubjects=requestedSubjects.Where(x=>x!=unclassified).ToArray();
        }
        foreach(Guid subject in requestedSubjects)Execute(c,tx,"insert into studio_media_asset_subjects values($1,$2)",id,subject);
        if(string.IsNullOrWhiteSpace(request.Collection))Execute(c,tx,"delete from studio_job_asset_collections where job_id=$1 and asset_key=$2",job,key);
        else {EnsureCollection(c,tx,job,request.Collection.Trim());Execute(c,tx,"insert into studio_job_asset_collections values($1,$2,$3) on conflict(job_id,asset_key) do update set collection_name=excluded.collection_name",job,key,request.Collection.Trim());}
        Audit(c,tx,job,"MediaChanged",user,"Updated image subjects, collection, tags and production state.");tx.Commit();
    }
    public static string[] NormalizeTags(IEnumerable<string> values)
    {
        var tags=values.Select(value=>(value??"").Trim()).Where(value=>value.Length>0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if(tags.Length>50||tags.Any(value=>value.Length>80))throw new ArgumentException("Use at most 50 tags, with up to 80 characters per tag.");
        return tags;
    }
    private static Guid EnsureUnclassifiedSubject(NpgsqlConnection c,NpgsqlTransaction tx,Guid job)
    {
        object? existing=Scalar(c,tx,"select id from studio_job_subjects where job_id=$1 and is_system and lower(name)='unclassified' limit 1",job);
        if(existing is Guid id)return id;
        Guid created=Guid.NewGuid();
        Execute(c,tx,"insert into studio_job_subjects(id,job_id,gallery_id,name,reference,notes,updated_by,collection_name,is_system) values($1,$2,$2,'Unclassified','','','System','Unclassified',true)",created,job);
        return created;
    }

    public void CreateCollection(Guid job,string name,string user)
    {
        ValidateCollection(name);Sync(job);using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        if(Scalar(c,tx,"select name from studio_media_collections where job_id=$1 and lower(name)=lower($2)",job,name.Trim()) is not null)throw new ArgumentException("Collection already exists.");
        EnsureCollection(c,tx,job,name.Trim());Audit(c,tx,job,"CollectionCreated",user,"Created collection "+name.Trim());tx.Commit();
    }
    public void RenameCollection(Guid job,string previous,string name,string user)
    {
        ValidateCollection(previous);ValidateCollection(name);using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        if(Scalar(c,tx,"select name from studio_media_collections where job_id=$1 and lower(name)=lower($2)",job,previous.Trim()) is not string original)throw new KeyNotFoundException();
        if(!original.Equals(name.Trim(),StringComparison.OrdinalIgnoreCase)&&Scalar(c,tx,"select name from studio_media_collections where job_id=$1 and lower(name)=lower($2)",job,name.Trim()) is not null)throw new ArgumentException("Collection already exists.");
        Execute(c,tx,"update studio_media_collections set name=$3 where job_id=$1 and name=$2",job,original,name.Trim());
        Execute(c,tx,"update studio_job_asset_collections set collection_name=$3 where job_id=$1 and collection_name=$2",job,original,name.Trim());
        Execute(c,tx,"update studio_job_subjects set collection_name=$3 where job_id=$1 and collection_name=$2",job,original,name.Trim());
        Execute(c,tx,"update studio_media_galleries set default_collection=$3 where id=$1 and default_collection=$2",job,original,name.Trim());
        Audit(c,tx,job,"CollectionRenamed",user,$"Renamed collection {original} to {name.Trim()}");tx.Commit();
    }
    public void SetDefaultCollection(Guid job,string name,string user)
    {
        ValidateCollection(name);using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        if(Scalar(c,tx,"select name from studio_media_collections where job_id=$1 and name=$2",job,name) is null)throw new KeyNotFoundException();
        Execute(c,tx,"update studio_media_galleries set default_collection=$2 where id=$1",job,name);
        Audit(c,tx,job,"DefaultCollectionChanged",user,"Default collection: "+name);tx.Commit();
    }
    private static void ValidateCollection(string? name)
    {
        if(string.IsNullOrWhiteSpace(name)||name.Trim().Length>100)throw new ArgumentException("Choose a collection name of at most 100 characters.");
    }
    private static void EnsureCollection(NpgsqlConnection c,NpgsqlTransaction tx,Guid job,string name)=>Execute(c,tx,"insert into studio_media_collections(job_id,name) values($1,$2) on conflict do nothing",job,name);
    public int ImportSubjects(Guid job,RosterImport request,string user)
    {
        var rows=MediaProductionRules.ReadRoster(request.Csv);var headers=rows[0];
        int name=Array.FindIndex(headers,x=>x.Equals(request.NameColumn,StringComparison.OrdinalIgnoreCase));
        int identifier=Array.FindIndex(headers,x=>x.Equals(request.IdColumn,StringComparison.OrdinalIgnoreCase));
        if(name<0||identifier<0)throw new ArgumentException("Select valid Name and Subject ID columns.");
        var data=rows.Skip(1).Select(row=>(Name:Cell(row,name),Id:Cell(row,identifier),Row:row)).ToList();
        if(data.Any(x=>string.IsNullOrWhiteSpace(x.Name)||x.Name.Equals("Unclassified",StringComparison.OrdinalIgnoreCase)||x.Name.Length>200||string.IsNullOrWhiteSpace(x.Id)||x.Id.Length>200)||data.Select(x=>x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=data.Count)
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
            string? Field(params string[] aliases)
            {
                string Key(string text)=>new string(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
                var matches=attributes.Where(p=>aliases.Select(Key).Contains(Key(p.Key))).ToArray();
                if(matches.Length>1)throw new ArgumentException("Ambiguous roster contact columns.");
                return matches.Length==0?null:matches[0].Value;
            }
            string? contactName=Field("ContactName"),contactEmail=Field("ContactEmail","Email"),contactPhone=Field("ContactPhone","Phone"),collection=Field("Collection");
            if((contactName?.Length??0)>200||(contactEmail?.Length??0)>320||(contactPhone?.Length??0)>100||(collection?.Length??0)>100||!string.IsNullOrEmpty(contactEmail)&&!System.Net.Mail.MailAddress.TryCreate(contactEmail,out _))throw new ArgumentException("Correct invalid roster contact fields before importing.");

            Execute(c,tx,"insert into studio_job_subjects(id,job_id,gallery_id,name,reference,notes,updated_by,attributes,collection_name) values($1,$2,$2,$3,$4,'',$5,$6::jsonb,coalesce((select default_collection from studio_media_galleries where id=$2),(select name from studio_media_collections where job_id=$2 order by name limit 1),'General')) on conflict(id) do update set name=excluded.name,attributes=excluded.attributes,updated_by=excluded.updated_by,updated_at_utc=now()",id,job,row.Name,row.Id,user,JsonSerializer.Serialize(attributes));
            Execute(c,tx,"update studio_job_subjects set contact_name=coalesce($2,contact_name),contact_email=coalesce($3,contact_email),contact_phone=coalesce($4,contact_phone),collection_name=coalesce(nullif($5,''),collection_name) where id=$1",id,(object?)contactName??DBNull.Value,(object?)contactEmail??DBNull.Value,(object?)contactPhone??DBNull.Value,(object?)collection??DBNull.Value);
        }
        Audit(c,tx,job,"RosterImported",user,$"Imported or updated {data.Count} subjects.");tx.Commit();return data.Count;
    }
    public JsonElement SessionPlan(Guid job)
    {
        using var c=database.OpenConnection();
        using var cmd=new NpgsqlCommand("""
            select coalesce(jsonb_agg(jsonb_build_object(
              'id',s.id,'name',s.name,'reference',s.reference,'collection',s.collection_name,
              'status',coalesce(p.status,s.production_status),'imageCount',coalesce(m.photos,0))
              order by s.collection_name,s.name,s.id),'[]'::jsonb)::text
            from studio_job_subjects s
            left join studio_workday_roster_progress p on p.id=s.id
            left join (select x.subject_id,count(*) as photos from studio_media_asset_subjects x join studio_media_assets a on a.id=x.asset_id where a.job_id=$1 and a.deleted_at_utc is null group by x.subject_id) m on m.subject_id=s.id
            where s.job_id=$1 and not s.is_system
            """,c);
        cmd.Parameters.AddWithValue(job);
        return JsonSerializer.Deserialize<JsonElement>((string)cmd.ExecuteScalar()!);
    }
    public void SubjectStatus(Guid job,Guid subject,string status,string user)
    {
        if(status!="Not Photographed")MediaProductionRules.ValidateState(status);
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        if(Execute(c,tx,"update studio_job_subjects set production_status=$3,updated_by=$4,updated_at_utc=now() where id=$1 and job_id=$2",subject,job,status,user)!=1)throw new KeyNotFoundException();
        Audit(c,tx,job,"SubjectStatusChanged",user,"Subject marked "+status);tx.Commit();
    }
    public byte[] Export(Guid job,Guid[] ids,string user)
    {using var file=ExportFile(job,ids,user);using var bytes=new MemoryStream();file.CopyTo(bytes);return bytes.ToArray();}
    public Stream ExportFile(Guid job,Guid[] ids,string user)
    {
        if(ids.Length==0||ids.Length>200)throw new ArgumentException("Select 1–200 images per editing batch.");
        Sync(job);using var c=database.OpenConnection();using var tx=c.BeginTransaction();Guid batch=Guid.NewGuid();
        Execute(c,tx,"insert into studio_media_edit_batches(id,job_id,created_by) values($1,$2,$3)",batch,job,user);
        var output=new FileStream(Path.Combine(Path.GetTempPath(),"studio-editing-"+Guid.NewGuid().ToString("N")+".zip"),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None,65536,FileOptions.DeleteOnClose);
        try
        {
        var manifest=new List<object>();long total=0;
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
        Audit(c,tx,job,"EditingBatchExported",user,$"Exported {manifest.Count} images for editing.");tx.Commit();output.Position=0;return output;
        }catch{output.Dispose();throw;}
    }
    public object Ingest(Guid job,IFormFile file,bool returned,Guid? explicitAsset,string user,string? note,string? collection=null,Guid? uploadId=null)
    {
        if(file.Length<=0||file.Length>100_000_000)throw new ArgumentException("Files must be between 1 byte and 100 MB.");
        if(!string.IsNullOrWhiteSpace(collection))ValidateCollection(collection);
        Sync(job);
        if(uploadId.HasValue)
        {
            using var existing=database.OpenConnection();
            using var q=new NpgsqlCommand("select id from studio_media_unmatched where id=$1 and job_id=$2",existing);q.Parameters.AddWithValue(uploadId.Value);q.Parameters.AddWithValue(job);
            if(q.ExecuteScalar() is Guid unmatchedId)return new{unmatched=true,id=unmatchedId,reused=true};
        }
        using var memory=new MemoryStream();using(var source=file.OpenReadStream())source.CopyTo(memory);
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
            using var c=database.OpenConnection();using var tx=c.BeginTransaction();StudioVersionStore.LockJobForMediaChange(c,tx,job);Guid id=uploadId??Guid.NewGuid();
            string key="unmatched/"+id.ToString("N");memory.Position=0;var version=versions.Add(c,tx,job,key,file.FileName,file.ContentType,memory,user,note);
            Execute(c,tx,"insert into studio_media_unmatched(id,job_id,staging_key,version_id,reason) values($1,$2,$3,$4,'No unique retained export identity. Select the original image.')",id,job,key,version.Id);
            CommitUploadReceipt(c,tx,uploadId,version.Id,null,true);Audit(c,tx,job,"UnmatchedUpload",user,"Returned file requires manual matching.");tx.Commit();return new{unmatched=true,id};
        }
        using(var c=database.OpenConnection())using(var tx=c.BeginTransaction())
        {
            StudioVersionStore.LockJobForMediaChange(c,tx,job);
            if(!returned&&!asset.HasValue)
            {
                using var duplicate=new NpgsqlCommand("select v.id,a.id from studio_asset_versions v join studio_media_assets a on a.job_id=v.job_id and a.asset_key=v.asset_key where v.job_id=$1 and lower(v.sha256)=lower($2) and lower(v.file_name)=lower($3) order by v.created_at_utc limit 1",c,tx);
                duplicate.Parameters.AddWithValue(job);duplicate.Parameters.AddWithValue(hash);duplicate.Parameters.AddWithValue(Path.GetFileName(file.FileName));
                using var reader=duplicate.ExecuteReader();if(reader.Read()){var priorVersion=reader.GetGuid(0);var priorAsset=reader.GetGuid(1);reader.Close();CommitUploadReceipt(c,tx,uploadId,priorVersion,priorAsset,false);tx.Commit();return new{unmatched=false,id=priorVersion,assetId=priorAsset,reused=true};}
            }
            string key=asset.HasValue?AssetKey(c,tx,job,asset.Value):Guid.NewGuid().ToString("D");
            Execute(c,tx,"select pg_advisory_xact_lock(hashtextextended($1,0))",job.ToString("D")+":"+key);
            if(asset.HasValue&&Scalar(c,tx,"select version_id from studio_media_returns where job_id=$1 and sha256=$2 and asset_id=$3",job,hash,asset.Value) is Guid prior){CommitUploadReceipt(c,tx,uploadId,prior,asset,false);tx.Commit();return new{unmatched=false,id=prior,reused=true};}
            if(sourceVersion.HasValue && Scalar(c,tx,"select id from studio_asset_versions where job_id=$1 and asset_key=$2 order by version_number desc limit 1",job,key) is Guid latest && latest != sourceVersion.Value)
            {
                Guid unmatched=uploadId??Guid.NewGuid();string staging="unmatched/"+unmatched.ToString("N");memory.Position=0;
                var staged=versions.Add(c,tx,job,staging,file.FileName,file.ContentType,memory,user,note,sourceVersion);
                Execute(c,tx,"insert into studio_media_unmatched(id,job_id,staging_key,version_id,reason) values($1,$2,$3,$4,'The editing batch is older than the current version. Review and explicitly match to promote this return.')",unmatched,job,staging,staged.Id);
                CommitUploadReceipt(c,tx,uploadId,staged.Id,null,true);Audit(c,tx,job,"StaleEditingReturn",user,"Older editing batch requires explicit matching.");tx.Commit();return new{unmatched=true,id=unmatched};
            }
            // Existing immutable store serializes version counters; never overwrite its files.
            memory.Position=0;var version=versions.Add(c,tx,job,key,file.FileName,file.ContentType,memory,user,note,sourceVersion);
            Guid mediaId=asset??Guid.NewGuid();
            Execute(c,tx,"insert into studio_media_assets(id,job_id,gallery_id,asset_key) values($1,$2,$2,$3) on conflict(job_id,asset_key) do nothing",mediaId,job,key);
            if(!asset.HasValue)
            {
                Execute(c,tx,"insert into studio_media_asset_subjects(asset_id,subject_id) values($1,$2) on conflict do nothing",mediaId,EnsureUnclassifiedSubject(c,tx,job));
                string? destination=string.IsNullOrWhiteSpace(collection)?Scalar(c,tx,"select coalesce(default_collection,(select name from studio_media_collections where job_id=$1 order by name limit 1)) from studio_media_galleries where id=$1",job) as string:collection.Trim();
                if(destination is not null)
                {
                    if(Scalar(c,tx,"select name from studio_media_collections where job_id=$1 and name=$2",job,destination) is null)throw new ArgumentException("Collection was not found.");
                    Execute(c,tx,"insert into studio_job_asset_collections values($1,$2,$3) on conflict(job_id,asset_key) do update set collection_name=excluded.collection_name",job,key,destination);
                }
            }
            if(asset.HasValue){Execute(c,tx,"insert into studio_media_returns values($1,$2,$3,$4)",job,hash,asset.Value,version.Id);Execute(c,tx,"update studio_media_assets set production_status='Needs Review' where id=$1",asset.Value);}
            CommitUploadReceipt(c,tx,uploadId,version.Id,mediaId,false);Audit(c,tx,job,asset.HasValue?"EditedVersionReturned":"MediaUploaded",user,Path.GetFileName(file.FileName));tx.Commit();return new{unmatched=false,id=version.Id,assetId=mediaId};
        }
    }
    // Commit the receipt with immutable version metadata: a lost response can be replayed even after Job closure.
    private static void CommitUploadReceipt(NpgsqlConnection c,NpgsqlTransaction tx,Guid? upload,Guid version,Guid? asset,bool unmatched)
    {
        if(upload is not Guid id)return;
        if(asset is Guid assetId)
        {
            Execute(c,tx,"insert into studio_media_asset_subjects(asset_id,subject_id) select $2,(b.declaration->>'subjectId')::uuid from studio_media_upload_receipts r join studio_media_upload_batches b on b.id=r.batch_id where r.id=$1 and b.declaration->>'subjectId' is not null on conflict do nothing",id,assetId);
            Execute(c,tx,"delete from studio_media_asset_subjects x using studio_job_subjects s where x.subject_id=s.id and x.asset_id=$2 and s.is_system and lower(s.name)='unclassified' and exists(select 1 from studio_media_upload_receipts r join studio_media_upload_batches b on b.id=r.batch_id where r.id=$1 and b.declaration->>'subjectId' is not null and (b.declaration->>'subjectId')::uuid<>s.id)",id,assetId);
        }
        Execute(c,tx,"update studio_media_upload_receipts set status=$2,version_id=$3,asset_id=$4,last_error=null,completed_at_utc=now() where id=$1",id,unmatched?"Unmatched":"Stored",version,(object?)asset??DBNull.Value);
    }
    public void Resolve(Guid job,Guid unmatched,Guid asset,string user)
    {
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        string key=AssetKey(c,tx,job,asset);
        using var cmd=new NpgsqlCommand("select version_id from studio_media_unmatched where id=$1 and job_id=$2 and resolved_asset_id is null for update",c,tx);cmd.Parameters.AddWithValue(unmatched);cmd.Parameters.AddWithValue(job);
        if(cmd.ExecuteScalar() is not Guid versionId)throw new ArgumentException("Upload is already matched or missing.");
        var original=versions.GetWithinTransaction(c,tx,job,versionId:versionId);using var stream=versions.OpenStored(original);var promoted=versions.Add(c,tx,job,key,original.FileName,original.ContentType,stream,user,"Manually matched returned image",versionId);
        Execute(c,tx,"update studio_media_unmatched set resolved_asset_id=$2 where id=$1",unmatched,asset);Execute(c,tx,"update studio_media_assets set production_status='Needs Review' where id=$1",asset);
        Execute(c,tx,"update studio_media_upload_receipts set status='Stored',asset_id=$2,resolved_version_id=$3 where version_id=$1",versionId,asset,promoted.Id);
        Audit(c,tx,job,"ReturnedImageMatched",user,"Unmatched upload associated with original asset.");tx.Commit();
    }
    private static string Cell(string[] row,int index)=>index<row.Length?row[index]:"";
    private static string AssetKey(NpgsqlConnection c,NpgsqlTransaction tx,Guid job,Guid asset)=>Scalar(c,tx,"select asset_key from studio_media_assets where id=$1 and job_id=$2",asset,job) as string??throw new ArgumentException("Image is missing or belongs to another job.");
    internal static void Audit(NpgsqlConnection c,NpgsqlTransaction tx,Guid job,string action,string user,string detail)=>Execute(c,tx,"insert into studio_operational_audit(id,job_id,action,user_name,detail,created_at_utc) values($1,$2,$3,$4,$5,now())",Guid.NewGuid(),job,action,user,detail);
    internal static int Execute(NpgsqlConnection c,NpgsqlTransaction tx,string sql,params object[] args){using var cmd=new NpgsqlCommand(sql,c,tx);foreach(var value in args)cmd.Parameters.AddWithValue(value);return cmd.ExecuteNonQuery();}
    internal static object? Scalar(NpgsqlConnection c,NpgsqlTransaction tx,string sql,params object[] args){using var cmd=new NpgsqlCommand(sql,c,tx);foreach(var value in args)cmd.Parameters.AddWithValue(value);return cmd.ExecuteScalar();}
}
public sealed record MediaAssetUpdate(string Status,string? Collection,Guid[]? Subjects,string? AssignedTo,DateTimeOffset? CaptureAtUtc=null,string[]? Tags=null);
public sealed record RosterImport(string Csv,string NameColumn,string IdColumn);
public sealed record SubjectStateRequest(string Status);
public sealed record MediaExportRequest(Guid[] Assets);
public sealed record MatchUploadRequest(Guid AssetId);


public sealed record MediaPageQuery(string? Collection=null,Guid? SubjectId=null,string? Status=null,Guid? After=null,int Limit=100,string? SubjectSearch=null,Guid? AssetId=null)
{
    public void Validate()
    {
        if(Limit<1||Limit>200)throw new ArgumentException("Choose a page size between 1 and 200.");
        if((Collection?.Length??0)>100||(SubjectSearch?.Length??0)>200)throw new ArgumentException("Filter is too long.");
        if(Status is not null)MediaProductionRules.ValidateState(Status);
    }
}


