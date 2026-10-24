using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Aegis.Studio.Web.Media;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Scheduling;
using Aegis.Studio.Web.Versioning;
using Aegis.Studio.Web.Workflow;
using Common.Storage;

namespace Aegis.Studio.Web.WorkDay;

public sealed class RemoteMediaImportWorker(IServiceScopeFactory scopes,IConfiguration configuration,ILogger<RemoteMediaImportWorker> logger):BackgroundService
{
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public const string Schema="""
      create table if not exists studio_remote_media_results(upload_id uuid primary key,result jsonb not null,created_at_utc timestamptz not null default now());
      alter table studio_remote_media_results add column if not exists metadata jsonb not null default '{}';
      alter table studio_remote_media_results add column if not exists job_id uuid;
      create table if not exists studio_remote_media_retries(id uuid primary key,upload_id uuid not null,job_id uuid not null,actor text not null,reason text not null,processed boolean not null default false);
      create unique index if not exists ix_remote_media_pending_retry on studio_remote_media_retries(upload_id) where not processed;
      """;
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        string? key=configuration["WorkDay:ApiSyncKey"];
        if(string.IsNullOrWhiteSpace(key))return;
        using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){BaseAddress=new("https://api.bryanstudiosltd.com/api/v1/"),Timeout=TimeSpan.FromMinutes(5)};
        http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",key);
        while(!ct.IsCancellationRequested)
        {
            try
            {
                await SendRetries(http,ct);
                var batch=await http.GetFromJsonAsync<RemoteMediaBatch>("admin/workday/remote-media",Json,ct);
                foreach(var item in batch?.Items??[])
                {
                    try{await Import(http,item,ct);}
                    catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
                    catch(Exception ex){logger.LogWarning("Remote media import retained for retry. UploadId={UploadId} FailureType={FailureType}",item.UploadId,ex.GetType().Name);}
                }
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception ex){logger.LogWarning("Remote media exchange will retry. FailureType={FailureType}",ex.GetType().Name);}
            try{await Task.Delay(TimeSpan.FromSeconds(20),ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
        }
    }
    async Task SendRetries(HttpClient http,CancellationToken ct)
    {
        using var scope=scopes.CreateScope();var db=scope.ServiceProvider.GetRequiredService<StudioDatabase>();using var c=db.OpenConnection();
        var pending=new List<(Guid Id,Guid Upload,Guid Job,string Actor,string Reason)>();
        using(var q=WorkflowStore.Command(c,null,"select id,upload_id,job_id,actor,reason from studio_remote_media_retries where not processed limit 20"))
        using(var r=q.ExecuteReader())while(r.Read())pending.Add((r.GetGuid(0),r.GetGuid(1),r.GetGuid(2),r.GetString(3),r.GetString(4)));
        foreach(var p in pending)
        {
            WorkflowStore.Sql(c,null,"select pg_advisory_lock(hashtextextended($1,0))","remote-media:"+p.Upload);
            try
            {
            using var response=await http.PostAsJsonAsync($"admin/workday/remote-media/{p.Upload:D}/retry",new{retryId=p.Id,actor=p.Actor,reason=p.Reason},ct);
            response.EnsureSuccessStatusCode();
            using var tx=c.BeginTransaction();
            WorkflowStore.Sql(c,tx,"delete from studio_remote_media_results where upload_id=$1 and result->>'state'='NeedsReview'",p.Upload);
            WorkflowStore.Sql(c,tx,"update studio_remote_media_retries set processed=true where id=$1",p.Id);
            Aegis.Studio.Web.Galleries.GalleryExchangeStore.Record(c,tx,p.Upload,p.Job,"RemoteMediaRetrySynchronized",p.Actor,JsonSerializer.Serialize(new{retryId=p.Id},Json));
            tx.Commit();
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
            catch(Exception ex){logger.LogWarning("Remote media retry remains queued. UploadId={UploadId} RetryId={RetryId} FailureType={FailureType}",p.Upload,p.Id,ex.GetType().Name);}
            finally{WorkflowStore.Sql(c,null,"select pg_advisory_unlock(hashtextextended($1,0))","remote-media:"+p.Upload);}
        }
    }
    async Task Import(HttpClient http,RemoteMedia item,CancellationToken ct)
    {
        using var scope=scopes.CreateScope();var services=scope.ServiceProvider;
        var db=services.GetRequiredService<StudioDatabase>();using var c=db.OpenConnection();
        using var operation=DemoMaintenance.TryEnterOperation(db);if(operation is null)return;
        WorkflowStore.Sql(c,null,"select pg_advisory_lock(hashtextextended($1,0))","remote-media:"+item.UploadId);
        try
        {
            string? saved=WorkflowStore.Scalar(c,null,"select result::text from studio_remote_media_results where upload_id=$1",item.UploadId) as string;
            if(saved is null)
            {
                object outcome;
                try
                {
                    var existing=StoredReceipt(c,item);
                    if(existing is not null)outcome=existing;
                    else
                    {
                    var user=new ClaimsPrincipal(new ClaimsIdentity([new Claim("login_name",item.UserName),new Claim(ClaimTypes.NameIdentifier,item.UserName)],"WorkDayExchange"));
                    var dest=WorkDayMediaDestinations.Resolve(item.Destination.AssignmentId,item.UserName,user,db,services.GetRequiredService<ScheduleStore>(),services.GetRequiredService<JobAccessService>());
                    if(dest is null||dest.JobId!=item.Destination.JobId||dest.SessionId!=item.Destination.SessionId||
                        !dest.Collections.Contains(item.Destination.Collection)||item.Destination.SubjectId is Guid subject&&!dest.Subjects.Any(s=>s.Id==subject&&s.Collection==item.Destination.Collection))
                        throw new ArgumentException("DestinationPermissionChanged");
                    if(WorkflowStore.Exists(c,null,"select exists(select 1 from studio_asset_versions v join studio_media_assets a on a.job_id=v.job_id and a.asset_key=v.asset_key left join studio_job_asset_collections col on col.job_id=a.job_id and col.asset_key=a.asset_key where v.job_id=$1 and lower(v.sha256)=lower($2) and lower(v.file_name)=lower($3) and coalesce(col.collection_name,'Unclassified')<>$4)",dest.JobId,item.Sha256,item.FileName,item.Destination.Collection))
                        throw new ArgumentException("ExistingMediaHasAnotherCollection");
                    if(item.SizeBytes is <1 or >100_000_000)throw new ArgumentException("FileSizeRejected");
                    string path=Path.Combine(Path.GetTempPath(),"studio-remote-"+Guid.NewGuid().ToString("N"));
                    try
                    {
                        using(var response=await http.GetAsync($"admin/workday/remote-media/{item.UploadId:D}/content",HttpCompletionOption.ResponseHeadersRead,ct))
                        {
                            response.EnsureSuccessStatusCode();await using var input=await response.Content.ReadAsStreamAsync(ct);
                            await using var file=new FileStream(path,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None);
                            if(!OperatingSystem.IsWindows())File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite);
                            var buffer=new byte[1024*1024];long count=0;int n;
                            while((n=await input.ReadAsync(buffer,ct))>0){count+=n;if(count>item.SizeBytes)throw new InvalidDataException("Remote media exceeds declared size.");await file.WriteAsync(buffer.AsMemory(0,n),ct);}
                            file.Position=0;string actual=Convert.ToHexString(await SHA256.HashDataAsync(file,ct));
                            if(count!=item.SizeBytes||!actual.Equals(item.Sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Remote media checksum mismatch.");
                        }
                        var media=new MediaProductionStore(db,new StudioVersionStore(db,services.GetRequiredService<IFileStorage>()));
                        var receipts=new MediaUploadReceipts(db,media);
                        // Keep one canonical Studio receipt per file. The web batch remains a grouping of these imports.
                        receipts.Create(dest.JobId,new(item.UploadId,dest.SessionId,[new(item.UploadId,item.FileName,item.SizeBytes)],item.Destination.Collection,item.Destination.SubjectId),item.UserName,false);
                        await using var content=File.OpenRead(path);
                        var form=new FormFile(content,0,content.Length,"file",item.FileName){Headers=new HeaderDictionary(),ContentType=item.ContentType};
                        var result=JsonSerializer.SerializeToElement(receipts.Receive(dest.JobId,item.UploadId,form,item.UserName),Json);
                        if(result.GetProperty("unmatched").GetBoolean())throw new ArgumentException("MediaRequiresReview");
                        outcome=new{state="Imported",assetId=result.GetProperty("assetId").GetGuid(),versionId=result.GetProperty("id").GetGuid(),appliedAt=DateTimeOffset.UtcNow};
                    }
                    finally{if(File.Exists(path))File.Delete(path);}
                    }
                }
                catch(Exception ex)when(ex is ArgumentException or UnauthorizedAccessException or KeyNotFoundException)
                {outcome=new{state="NeedsReview",code=ex is UnauthorizedAccessException?"PermissionChanged":"DestinationOrMediaRequiresReview",appliedAt=DateTimeOffset.UtcNow};}
                saved=JsonSerializer.Serialize(outcome,Json);
                using var tx=c.BeginTransaction();
                WorkflowStore.Sql(c,tx,"insert into studio_remote_media_results(upload_id,result,metadata,job_id) values($1,$2::jsonb,$3::jsonb,$4) on conflict do nothing",item.UploadId,saved,JsonSerializer.Serialize(item,Json),item.Destination.JobId);
                Aegis.Studio.Web.Galleries.GalleryExchangeStore.Record(c,tx,item.UploadId,item.Destination.JobId,"RemoteMediaImport",item.UserName,saved);
                tx.Commit();
            }
            using var ack=await http.PostAsJsonAsync($"admin/workday/remote-media/{item.UploadId:D}/result",JsonSerializer.Deserialize<JsonElement>(saved),ct);
            ack.EnsureSuccessStatusCode();
            logger.LogInformation("Remote media import result acknowledged. UploadId={UploadId} BatchId={BatchId} JobId={JobId}",item.UploadId,item.BatchId,item.Destination.JobId);
        }
        finally{WorkflowStore.Sql(c,null,"select pg_advisory_unlock(hashtextextended($1,0))","remote-media:"+item.UploadId);}
    }
    static object? StoredReceipt(NpgsqlConnection c,RemoteMedia item)
    {
        using var q=WorkflowStore.Command(c,null,"select r.version_id,r.asset_id,r.file_hash,r.completed_at_utc,r.job_id,b.created_by,b.declaration::text from studio_media_upload_receipts r join studio_media_upload_batches b on b.id=r.batch_id where r.id=$1 and r.status='Stored'",item.UploadId);
        using var r=q.ExecuteReader();if(!r.Read())return null;
        var declaration=JsonSerializer.Deserialize<UploadBatchDeclaration>(r.GetString(6),Json)!;
        if(r.GetGuid(4)!=item.Destination.JobId||!r.GetString(5).Equals(item.UserName,StringComparison.OrdinalIgnoreCase)||
            !r.GetString(2).Equals(item.Sha256,StringComparison.OrdinalIgnoreCase)||declaration.Collection!=item.Destination.Collection||declaration.SubjectId!=item.Destination.SubjectId)
            throw new InvalidDataException("Stored media receipt does not match the remote upload.");
        return new{state="Imported",assetId=r.GetGuid(1),versionId=r.GetGuid(0),appliedAt=r.GetFieldValue<DateTimeOffset>(3)};
    }
    sealed record RemoteMediaBatch(IReadOnlyList<RemoteMedia> Items);
    sealed record RemoteMedia(Guid UploadId,Guid BatchId,string UserName,string FileName,string ContentType,long SizeBytes,string Sha256,RemoteDestination Destination);
    sealed record RemoteDestination(Guid AssignmentId,Guid JobId,Guid? SessionId,string Collection,Guid? SubjectId);
}

public static class RemoteMediaReviewEndpoints
{
    public static void MapRemoteMediaReview(this WebApplication app)
    {
        app.MapGet("/api/jobs/{job:guid}/remote-media",(Guid job,ClaimsPrincipal user,StudioDatabase db,JobAccessService access,WorkflowStore workflow)=>
        {
            if(!access.HasPermission(user,job,"ViewAttachments"))return Results.Forbid();
            using var c=db.OpenConnection();
            return Results.Json(JsonSerializer.Deserialize<JsonElement>((string)WorkflowStore.Scalar(c,null,"select coalesce(jsonb_agg(jsonb_build_object('uploadId',upload_id,'result',result,'metadata',metadata,'createdAt',created_at_utc,'canRetry',$2::boolean) order by created_at_utc desc),'[]'::jsonb)::text from studio_remote_media_results where job_id=$1",job,workflow.CanManage(user,job)||access.HasPermission(user,job,"ManageProduction"))!));
        }).RequireAuthorization();
        app.MapPost("/api/jobs/{job:guid}/remote-media/{upload:guid}/retry",(Guid job,Guid upload,RemoteMediaRetry request,ClaimsPrincipal user,StudioDatabase db,JobAccessService access,WorkflowStore workflow)=>
        {
            if(!workflow.CanManage(user,job)&&!access.HasPermission(user,job,"ManageProduction"))return Results.Forbid();
            if(string.IsNullOrWhiteSpace(request.Reason)||request.Reason.Length>1000)return Results.BadRequest(new{message="Record the correction made before retrying."});
            using var c=db.OpenConnection();using var tx=c.BeginTransaction();
            WorkflowStore.Sql(c,tx,"select pg_advisory_xact_lock(hashtextextended($1,0))","remote-media-retry:"+upload);
            if(!WorkflowStore.Exists(c,tx,"select exists(select 1 from studio_remote_media_results where upload_id=$1 and job_id=$2 and result->>'state'='NeedsReview')",upload,job))return Results.NotFound();
            if(WorkflowStore.Scalar(c,tx,"select id from studio_remote_media_retries where upload_id=$1 and not processed",upload) is Guid queued)return Results.Ok(new{retryId=queued,state="Retry queued"});
            Guid id=Guid.NewGuid();string actor=WorkflowStore.Name(user);
            WorkflowStore.Sql(c,tx,"insert into studio_remote_media_retries(id,upload_id,job_id,actor,reason) values($1,$2,$3,$4,$5)",id,upload,job,actor,request.Reason.Trim());
            Aegis.Studio.Web.Galleries.GalleryExchangeStore.Record(c,tx,upload,job,"RemoteMediaRetryRequested",actor,JsonSerializer.Serialize(new{retryId=id,reason=request.Reason.Trim()}));
            tx.Commit();return Results.Ok(new{retryId=id,state="Retry queued"});
        }).RequireAuthorization();
    }
    public sealed record RemoteMediaRetry(string Reason);
}
