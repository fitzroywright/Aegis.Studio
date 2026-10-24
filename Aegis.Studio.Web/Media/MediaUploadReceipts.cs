using System.Security.Cryptography;
using System.Text.Json;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Versioning;
using Aegis.Studio.Web.Workflow;
using Npgsql;
namespace Aegis.Studio.Web.Media;

public sealed record UploadFileDeclaration(Guid UploadId,string FileName,long SizeBytes);
public sealed record UploadBatchDeclaration(Guid BatchId,Guid? SessionId,IReadOnlyList<UploadFileDeclaration> Files,string? Collection=null,Guid? SubjectId=null,bool Returned=false,Guid? AssetId=null,string? Note=null);
public sealed class MediaUploadReceipts(StudioDatabase database,MediaProductionStore media)
{
    private static readonly JsonSerializerOptions json=new(JsonSerializerDefaults.Web);
    public const string Schema="""
        create table if not exists studio_media_upload_batches(
          id uuid primary key,job_id uuid not null references studio_jobs(id),session_id uuid,created_by text not null,
          declaration jsonb not null,created_at_utc timestamptz not null default now());
        create table if not exists studio_media_upload_receipts(
          id uuid primary key,batch_id uuid not null references studio_media_upload_batches(id),job_id uuid not null references studio_jobs(id),
          file_name text not null,size_bytes bigint not null,file_hash text,
          status text not null default 'Pending',version_id uuid references studio_asset_versions(id),asset_id uuid,
          resolved_version_id uuid references studio_asset_versions(id),last_error text,received_at_utc timestamptz,completed_at_utc timestamptz);
        alter table studio_media_upload_batches add column if not exists cancelled_at_utc timestamptz;
        alter table studio_media_upload_batches add column if not exists cancelled_by text;
        alter table studio_media_upload_batches add column if not exists cancellation_reason text;
        create index if not exists ix_media_upload_batch_job on studio_media_upload_batches(job_id,created_at_utc desc);
        create index if not exists ix_media_upload_receipt_batch on studio_media_upload_receipts(batch_id,status);
        """;
    public object Create(Guid job,UploadBatchDeclaration declaration,string actor,bool canManage)
    {
        Validate(declaration);
        declaration=declaration with{Files=declaration.Files.Select(f=>f with{FileName=Path.GetFileName(f.FileName)}).ToArray()};
        string facts=JsonSerializer.Serialize(declaration,json);
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        WorkflowStore.Sql(c,tx,"select pg_advisory_xact_lock(hashtextextended($1,0))","upload-batch:"+declaration.BatchId);
        using(var prior=WorkflowStore.Command(c,tx,"select job_id,created_by,declaration::text from studio_media_upload_batches where id=$1",declaration.BatchId))
        using(var r=prior.ExecuteReader())
        {
            if(r.Read())
            {
                if(r.GetGuid(0)!=job||!r.GetString(1).Equals(actor,StringComparison.OrdinalIgnoreCase)||JsonSerializer.Serialize(JsonSerializer.Deserialize<UploadBatchDeclaration>(r.GetString(2),json),json)!=facts)
                    throw new ArgumentException("This upload batch ID was used for different files or another reporter.");
                return new{batchId=declaration.BatchId,expectedFiles=declaration.Files.Count,reused=true};
            }
        }
        StudioVersionStore.LockJobForMediaChange(c,tx,job);
        bool productionReturn=declaration.Returned&&WorkflowStore.Exists(c,tx,"select exists(select 1 from studio_job_activities where job_id=$1 and lower(assigned_to)=lower($2) and activity_type in ('Edit','Retouch','ProductionExport') and status not in ('Completed','Cancelled','Archived'))",job,actor);
        if(declaration.SessionId is null&&WorkflowStore.Exists(c,tx,"select exists(select 1 from studio_workflow_sessions where job_id=$1 and required)",job)&&!productionReturn)throw new ArgumentException("Choose the photography session for this upload batch.");
        if(declaration.SessionId is Guid session&&!WorkflowStore.Exists(c,tx,"select exists(select 1 from studio_workflow_sessions s where s.id=$1 and s.job_id=$2 and ($4 or exists(select 1 from studio_workflow_crew crew where crew.session_id=s.id and lower(crew.user_name)=lower($3))))",session,job,actor,canManage||productionReturn))
            throw new ArgumentException("Choose a session in this Job where you are assigned, or obtain Job coordination authority.");
        if(declaration.SubjectId is Guid subject&&!WorkflowStore.Exists(c,tx,"select exists(select 1 from studio_job_subjects where id=$1 and job_id=$2)",subject,job))throw new ArgumentException("Choose a subject in this Job.");
        if(!string.IsNullOrWhiteSpace(declaration.Collection)&&declaration.Collection!="Unclassified"&&!WorkflowStore.Exists(c,tx,"select exists(select 1 from studio_media_collections where job_id=$1 and name=$2)",job,declaration.Collection))throw new ArgumentException("Choose a collection in this Job.");
        WorkflowStore.Sql(c,tx,"insert into studio_media_upload_batches(id,job_id,session_id,created_by,declaration) values($1,$2,$3,$4,$5::jsonb)",declaration.BatchId,job,declaration.SessionId,actor,facts);
        foreach(var f in declaration.Files)WorkflowStore.Sql(c,tx,"insert into studio_media_upload_receipts(id,batch_id,job_id,file_name,size_bytes) values($1,$2,$3,$4,$5)",f.UploadId,declaration.BatchId,job,f.FileName,f.SizeBytes);
        tx.Commit();return new{batchId=declaration.BatchId,expectedFiles=declaration.Files.Count,reused=false};
    }
    public object Receive(Guid job,Guid uploadId,IFormFile file,string actor)
    {
        using var c=database.OpenConnection();
        // Session-level lock survives the separate existing ingest transaction.
        WorkflowStore.Sql(c,null,"select pg_advisory_lock(hashtextextended($1,0))","media-upload:"+uploadId);
        try
        {
            Guid batch;string state;string? previousHash;UploadBatchDeclaration declaration;Guid? versionId,asset;
            using(var q=WorkflowStore.Command(c,null,"select r.batch_id,r.file_name,r.size_bytes,r.status,r.file_hash,b.created_by,b.declaration::text,r.version_id,r.asset_id from studio_media_upload_receipts r join studio_media_upload_batches b on b.id=r.batch_id where r.id=$1 and r.job_id=$2",uploadId,job))
            using(var r=q.ExecuteReader())
            {
                if(!r.Read())throw new ArgumentException("Declare the upload batch before sending its files.");
                if(!r.GetString(5).Equals(actor,StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException();
                if(r.GetString(1)!=Path.GetFileName(file.FileName)||r.GetInt64(2)!=file.Length)throw new ArgumentException("The file does not match its declared upload identity.");
                batch=r.GetGuid(0);state=r.GetString(3);previousHash=r.IsDBNull(4)?null:r.GetString(4);declaration=JsonSerializer.Deserialize<UploadBatchDeclaration>(r.GetString(6),json)!;
                versionId=r.IsDBNull(7)?null:r.GetGuid(7);asset=r.IsDBNull(8)?null:r.GetGuid(8);
            }
            if(state=="Cancelled")throw new ArgumentException("This upload was explicitly cancelled. Start a new batch for replacement files.");
            using var input=file.OpenReadStream();string hash=Convert.ToHexString(SHA256.HashData(input));
            if(previousHash is not null&&previousHash!=hash)throw new ArgumentException("This upload ID belongs to different file bytes. Start a new upload for the changed file.");
            if(state is "Stored" or "Unmatched")return new{uploadId,batchId=batch,id=versionId,assetId=asset,unmatched=state=="Unmatched",state,reused=true};
            WorkflowStore.Sql(c,null,"update studio_media_upload_receipts set status='Uploading',file_hash=$2,last_error=null,received_at_utc=coalesce(received_at_utc,now()) where id=$1",uploadId,hash);
            try
            {
                var result=JsonSerializer.SerializeToElement(media.Ingest(job,file,declaration.Returned,declaration.AssetId,actor,declaration.Note,declaration.Collection,uploadId),json);
                bool unmatched=result.GetProperty("unmatched").GetBoolean();Guid identity=result.GetProperty("id").GetGuid();
                using(var lookup=WorkflowStore.Command(c,null,unmatched?
                    "select v.id,v.sha256,v.size_bytes,null::uuid from studio_media_unmatched u join studio_asset_versions v on v.id=u.version_id where u.id=$1 and u.job_id=$2":
                    "select v.id,v.sha256,v.size_bytes,a.id from studio_asset_versions v join studio_media_assets a on a.job_id=v.job_id and a.asset_key=v.asset_key where v.id=$1 and v.job_id=$2",identity,job))
                using(var r=lookup.ExecuteReader())
                {
                    if(!r.Read()||!r.GetString(1).Equals(hash,StringComparison.OrdinalIgnoreCase)||r.GetInt64(2)!=file.Length)throw new InvalidOperationException("The stored-version receipt does not match this upload.");
                    versionId=r.GetGuid(0);asset=r.IsDBNull(3)?null:r.GetGuid(3);
                }
                using var tx=c.BeginTransaction();
                state=unmatched?"Unmatched":"Stored";
                WorkflowStore.Sql(c,tx,"update studio_media_upload_receipts set status=$2,version_id=$3,asset_id=$4,last_error=null,completed_at_utc=now() where id=$1",uploadId,state,versionId,asset);
                tx.Commit();return new{uploadId,batchId=batch,id=versionId,assetId=asset,unmatched,state,reused=result.TryGetProperty("reused",out var reused)&&reused.GetBoolean()};
            }
            catch(Exception ex)
            {
                WorkflowStore.Sql(c,null,"update studio_media_upload_receipts set status='Failed',last_error=$2 where id=$1 and status not in ('Stored','Unmatched')",uploadId,ex.GetType().Name);throw;
            }
        }
        finally{WorkflowStore.Sql(c,null,"select pg_advisory_unlock(hashtextextended($1,0))","media-upload:"+uploadId);}
    }
    public object List(Guid job)
    {
        using var c=database.OpenConnection();
        using var q=WorkflowStore.Command(c,null,"select b.id,b.session_id,b.created_by,b.created_at_utc,b.declaration::text,b.cancelled_at_utc,b.cancellation_reason,coalesce(jsonb_agg(jsonb_build_object('uploadId',r.id,'fileName',r.file_name,'sizeBytes',r.size_bytes,'state',r.status,'versionId',r.version_id,'assetId',r.asset_id,'resolvedVersionId',r.resolved_version_id,'error',r.last_error) order by r.file_name) filter(where r.id is not null),'[]'::jsonb)::text from studio_media_upload_batches b left join studio_media_upload_receipts r on r.batch_id=b.id where b.job_id=$1 group by b.id order by b.created_at_utc desc limit 100",job);
        using var r=q.ExecuteReader();var rows=new List<object>();
        while(r.Read())rows.Add(new{batchId=r.GetGuid(0),sessionId=r.IsDBNull(1)?(Guid?)null:r.GetGuid(1),createdBy=r.GetString(2),createdAtUtc=r.GetFieldValue<DateTimeOffset>(3),declaration=JsonSerializer.Deserialize<UploadBatchDeclaration>(r.GetString(4),json),cancelledAtUtc=r.IsDBNull(5)?(DateTimeOffset?)null:r.GetFieldValue<DateTimeOffset>(5),cancellationReason=r.IsDBNull(6)?null:r.GetString(6),files=JsonSerializer.Deserialize<JsonElement>(r.GetString(7))});
        return rows;
    }
    public object Cancel(Guid job,Guid batch,string actor,bool canManage,string reason)
    {
        if(string.IsNullOrWhiteSpace(reason)||reason.Length>4000)throw new ArgumentException("Explain why the remaining declared files are cancelled (maximum 4000 characters).");
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        var ids=new List<Guid>();
        using(var q=WorkflowStore.Command(c,tx,"select r.id from studio_media_upload_receipts r join studio_media_upload_batches b on b.id=r.batch_id where b.id=$1 and b.job_id=$2 and ($4 or lower(b.created_by)=lower($3)) order by r.id",batch,job,actor,canManage))
        using(var r=q.ExecuteReader()){while(r.Read())ids.Add(r.GetGuid(0));}
        if(ids.Count==0)throw new UnauthorizedAccessException();
        // Acquire the same per-file locks before the Job lock; an in-flight writer must finish first.
        foreach(var id in ids)WorkflowStore.Sql(c,tx,"select pg_advisory_xact_lock(hashtextextended($1,0))","media-upload:"+id);
        StudioVersionStore.LockJobForMediaChange(c,tx,job);
        WorkflowStore.Sql(c,tx,"update studio_media_upload_batches set cancelled_at_utc=now(),cancelled_by=$3,cancellation_reason=$4 where id=$1 and job_id=$2 and cancelled_at_utc is null",batch,job,actor,reason.Trim());
        WorkflowStore.Sql(c,tx,"update studio_media_upload_receipts set status='Cancelled' where batch_id=$1 and status not in ('Stored','Cancelled')",batch);
        WorkflowStore.Sql(c,tx,"update studio_workflow_upload_batches set invalidated_at_utc=now() where source_upload_batch_id=$1 and invalidated_at_utc is null",batch);
        WorkflowStore.Sql(c,tx,"update studio_jobs set workflow_revision=workflow_revision+1 where id=$1",job);
        MediaProductionStore.Audit(c,tx,job,"UploadBatchCancelled",actor,$"Batch {batch}: {reason.Trim()}. Stored versions retained; replacement material needs a new declared batch.");
        tx.Commit();return new{batchId=batch,state="Cancelled"};
    }
    public static void Validate(UploadBatchDeclaration declaration)
    {
        if(declaration.BatchId==Guid.Empty||declaration.Files is null||declaration.Files.Count is <1 or >10000||declaration.Files.Select(f=>f.UploadId).Distinct().Count()!=declaration.Files.Count)
            throw new ArgumentException("Declare between 1 and 10,000 distinct upload identities.");
        if(declaration.Files.Any(f=>f.UploadId==Guid.Empty||string.IsNullOrWhiteSpace(f.FileName)||Path.GetFileName(f.FileName).Length>255||f.SizeBytes is <1 or >100_000_000))throw new ArgumentException("Each declared file needs a stable ID, filename and size between 1 byte and 100 MB.");
        if((declaration.Note?.Length??0)>4000)throw new ArgumentException("Upload notes may contain at most 4000 characters.");
    }
}

