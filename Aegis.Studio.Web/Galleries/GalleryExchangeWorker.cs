using System.Text.Json;
using System.Security.Claims;
using Aegis.Studio.Web.Intake;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Workflow;
using Common.Storage;
using Npgsql;

namespace Aegis.Studio.Web.Galleries;

public sealed class GalleryExchangeWorker(StudioDatabase database,IFileStorage storage,IConfiguration configuration,ILogger<GalleryExchangeWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if(!configuration.GetValue<bool>("PublicSite:GalleryExchangeEnabled"))return;
        while(!ct.IsCancellationRequested)
        {
            try
            {
                using var site=new PublicSiteClient(configuration);
                using var operation=DemoMaintenance.TryEnterOperation(database);
                if(operation is not null)
                {
                    using var c=database.OpenConnection();
                    if(WorkflowStore.Exists(c,null,"select pg_try_advisory_lock(72919351)"))try
                    {await ImportRequests(c,site,ct);await SendQueued(c,site,ct);await Reconcile(c,site,ct);}
                    finally{WorkflowStore.Sql(c,null,"select pg_advisory_unlock(72919351)");}
                }
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception ex){logger.LogWarning("Gallery exchange retained for retry. FailureType={FailureType}",ex.GetType().Name);}
            try{await Task.Delay(TimeSpan.FromSeconds(30),ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
        }
    }
    async Task ImportRequests(NpgsqlConnection c,PublicSiteClient site,CancellationToken ct)
    {
        long after=Convert.ToInt64(WorkflowStore.Scalar(c,null,"select coalesce((select value from studio_gallery_exchange_cursor where source='requests'),0)"));
        var feed=await site.Get("/api/v1/gallery-requests/changes?after="+after,ct);
        foreach(var p in feed.GetProperty("items").EnumerateArray())
        {
            Guid id=p.GetProperty("id").GetGuid();using var tx=c.BeginTransaction();
            bool exists=WorkflowStore.Exists(c,tx,"select exists(select 1 from studio_gallery_requests where id=$1)",id);
            if(!exists)
            {
                WorkflowStore.Sql(c,tx,"insert into studio_gallery_requests(id,integration_id,external_reference,requester_reference,payload) values($1,$2,$3,$4,$5::jsonb)",
                    id,p.GetProperty("integrationId").GetString(),p.GetProperty("externalReference").GetString(),p.GetProperty("requesterReference").GetString(),p.GetRawText());
                GalleryExchangeStore.Record(c,tx,id,null,"GalleryRequestImported","API","{}");
            }
            WorkflowStore.Sql(c,tx,"insert into studio_gallery_exchange_cursor values('requests',$1) on conflict(source) do update set value=$1",p.GetProperty("sequence").GetInt64());
            tx.Commit();if(!exists)logger.LogInformation("Gallery request imported. RequestId={RequestId} IntegrationId={IntegrationId}",id,p.GetProperty("integrationId").GetString());
        }
    }
    async Task SendQueued(NpgsqlConnection c,PublicSiteClient site,CancellationToken ct)
    {
        var items=new List<(Guid Id,Guid Request,string Kind,string Payload,int Attempts)>();
        using(var q=WorkflowStore.Command(c,null,"""
          select o.id,o.request_id,o.kind,o.payload::text,o.attempts from studio_gallery_exchange_outbox o
          where o.status='Pending' and o.next_attempt_at_utc<=now()
            and not exists(select 1 from studio_gallery_exchange_outbox p where p.request_id=o.request_id and p.status='Pending' and p.sequence<o.sequence)
          order by o.sequence limit 20
          """))using(var r=q.ExecuteReader())while(r.Read())items.Add((r.GetGuid(0),r.GetGuid(1),r.GetString(2),r.GetString(3),r.GetInt32(4)));
        foreach(var item in items)
        {
            try
            {
                JsonElement p=JsonSerializer.Deserialize<JsonElement>(item.Payload);
                logger.LogInformation("Gallery exchange attempt. RequestId={RequestId} OperationId={OperationId} Action={Action} Attempt={Attempt}",item.Request,item.Id,item.Kind,item.Attempts+1);
                if(item.Kind=="Transfer")
                {
                    bool eligible=WorkflowStore.Exists(c,null,"select exists(select 1 from studio_gallery_requests where id=$1 and state='Approved' and decision_version=$2)",item.Request,p.GetProperty("decisionVersion").GetInt32());
                    if(!eligible)
                    {
                        using var superseded=c.BeginTransaction();WorkflowStore.Sql(c,superseded,"update studio_gallery_exchange_outbox set status='Superseded' where id=$1",item.Id);
                        WorkflowStore.Sql(c,superseded,"update studio_gallery_transfers set state='Superseded' where id=$1",item.Id);
                        GalleryExchangeStore.Record(c,superseded,item.Request,null,"GalleryTransferSuperseded","Studio",JsonSerializer.Serialize(new{transferId=item.Id}));
                        superseded.Commit();continue;
                    }
                    var staged=await site.PostJson("/api/v1/gallery-transfers",p,ct);
                    if(staged.GetProperty("state").GetString() is not ("Available" or "Completed"))
                    {
                    var stored=staged.GetProperty("storedVersionIds").EnumerateArray().Select(x=>x.GetGuid()).ToHashSet();
                    Guid job=(Guid)WorkflowStore.Scalar(c,null,"select job_id from studio_gallery_requests where id=$1",item.Request)!;
                    foreach(var file in p.GetProperty("items").EnumerateArray())
                    {
                        Guid version=file.GetProperty("versionId").GetGuid();if(stored.Contains(version))continue;
                        string? path=WorkflowStore.Scalar(c,null,"select v.stored_path from studio_asset_versions v join studio_media_assets a on a.job_id=v.job_id and a.asset_key=v.asset_key where v.id=$1 and v.job_id=$2 and a.approved_version_id=v.id",version,job) as string;
                        if(path is null)throw new InvalidOperationException("Approved media changed before transfer.");
                        await using var content=await storage.OpenReadAsync("VersionStore/"+path,ct);
                        await site.PutFile($"/api/v1/gallery-transfers/{item.Id:D}/files/{version:D}",content,file.GetProperty("contentType").GetString()!,ct);
                    }
                    // Recheck local withdrawal after potentially long file staging.
                    if(!WorkflowStore.Exists(c,null,"select exists(select 1 from studio_gallery_requests where id=$1 and state='Approved' and decision_version=$2)",item.Request,p.GetProperty("decisionVersion").GetInt32()))
                        throw new InvalidOperationException("Request changed during gallery staging.");
                    var required=p.GetProperty("items").EnumerateArray().Select(x=>x.GetProperty("versionId").GetGuid()).ToArray();
                    long approved=Convert.ToInt64(WorkflowStore.Scalar(c,null,"select count(*) from studio_media_assets a join studio_jobs j on j.id=a.job_id where a.job_id=$1 and a.approved_version_id=any($2) and j.status in ('Ready for Delivery','Delivered','Completed')",job,required));
                    if(approved!=required.Length)throw new InvalidOperationException("Approved media changed before activation.");
                    await site.Post($"/api/v1/gallery-transfers/{item.Id:D}/activate",new{},ct);
                    }
                }
                else if(item.Kind=="Decision")await site.Post($"/api/v1/gallery-requests/{item.Request:D}/decision",p,ct);
                else if(item.Kind=="Revocation")await site.Post("/api/v1/gallery-revocations",p,ct);
                else throw new InvalidOperationException("Unknown gallery exchange action.");
                using var tx=c.BeginTransaction();
                WorkflowStore.Sql(c,tx,"update studio_gallery_exchange_outbox set status='Sent',attempts=attempts+1,last_attempt_at_utc=now(),last_error=null where id=$1",item.Id);
                if(item.Kind=="Transfer")WorkflowStore.Sql(c,tx,"update studio_gallery_transfers set state='Available to external site' where id=$1 and exists(select 1 from studio_gallery_requests where id=$2 and state='Approved' and decision_version=$3)",item.Id,item.Request,p.GetProperty("decisionVersion").GetInt32());
                if(item.Kind=="Revocation")WorkflowStore.Sql(c,tx,"update studio_gallery_revocations set state='API received request' where id=$1",item.Id);
                GalleryExchangeStore.Record(c,tx,item.Request,null,"Gallery"+item.Kind+"Synchronized","Studio",JsonSerializer.Serialize(new{operationId=item.Id,attempt=item.Attempts+1}));
                tx.Commit();
                logger.LogInformation("Gallery exchange action synchronized. RequestId={RequestId} OperationId={OperationId} Action={Action}",item.Request,item.Id,item.Kind);
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
            catch(Exception ex)
            {
                using var tx=c.BeginTransaction();
                WorkflowStore.Sql(c,tx,"update studio_gallery_exchange_outbox set attempts=attempts+1,last_attempt_at_utc=now(),last_error=$2,next_attempt_at_utc=now()+interval '60 seconds' where id=$1",item.Id,ex.GetType().Name);
                GalleryExchangeStore.Record(c,tx,item.Request,null,"GalleryExchangeAttemptFailed","Studio",JsonSerializer.Serialize(new{operationId=item.Id,attempt=item.Attempts+1,code=ex.GetType().Name}));
                tx.Commit();
                logger.LogWarning("Gallery exchange action will retry. RequestId={RequestId} OperationId={OperationId} Action={Action} Attempt={Attempt} FailureType={FailureType}",item.Request,item.Id,item.Kind,item.Attempts+1,ex.GetType().Name);
            }
        }
    }
    async Task Reconcile(NpgsqlConnection c,PublicSiteClient site,CancellationToken ct)
    {
        var ids=new List<(Guid Id,long After)>();
        using(var q=WorkflowStore.Command(c,null,"select id,last_remote_sequence from studio_gallery_requests order by last_polled_at_utc nulls first limit 50"))
        using(var r=q.ExecuteReader())while(r.Read())ids.Add((r.GetGuid(0),r.GetInt64(1)));
        foreach(var item in ids)
        {
            Guid id=item.Id;
            try
            {
            var state=await site.Get($"/api/v1/admin/gallery-exchange/state?requestId={id:D}&after={item.After}",ct);
            using var tx=c.BeginTransaction();
            WorkflowStore.Sql(c,tx,"update studio_gallery_requests set remote_state=$2::jsonb,last_polled_at_utc=now(),poll_error=null where id=$1",id,state.GetRawText());
            bool pending=WorkflowStore.Exists(c,tx,"select exists(select 1 from studio_gallery_exchange_outbox where request_id=$1 and status='Pending')",id);
            if(!pending)foreach(var t in state.GetProperty("transfers").EnumerateArray())
                WorkflowStore.Sql(c,tx,"update studio_gallery_transfers set state=$2 where id=$1",t.GetProperty("id").GetGuid(),t.GetProperty("state").GetString());
            foreach(var r in state.GetProperty("revocations").EnumerateArray())
                WorkflowStore.Sql(c,tx,"update studio_gallery_revocations set state=$2 where id=$1",r.GetProperty("id").GetGuid(),r.GetProperty("state").GetString());
            foreach(var a in state.GetProperty("activity").EnumerateArray())
            {
                Guid remote=a.GetProperty("id").GetGuid();
                WorkflowStore.Sql(c,tx,"update studio_gallery_requests set last_remote_sequence=greatest(last_remote_sequence,$2) where id=$1",id,a.GetProperty("sequence").GetInt64());
                if(WorkflowStore.Exists(c,tx,"select exists(select 1 from studio_exchange_activity where remote_id=$1)",remote))continue;
                WorkflowStore.Sql(c,tx,"insert into studio_exchange_activity(id,aggregate_id,job_id,action,actor,details,occurred_at_utc,remote_id) select $1,$2,job_id,$3,$4,$5::jsonb,$6,$7 from studio_gallery_requests where id=$2",
                    Guid.NewGuid(),id,a.GetProperty("action").GetString(),a.GetProperty("actor").GetString(),a.GetProperty("details").GetRawText(),a.GetProperty("occurredAt").GetDateTimeOffset(),remote);
                logger.LogInformation("Gallery external activity recorded. RequestId={RequestId} RemoteEventId={RemoteEventId} Action={Action}",id,remote,a.GetProperty("action").GetString());
            }
            tx.Commit();
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
            catch(Exception ex)
            {
                WorkflowStore.Sql(c,null,"update studio_gallery_requests set last_polled_at_utc=now(),poll_error=$2 where id=$1",id,ex.GetType().Name);
                logger.LogWarning("Gallery status polling retained for retry. RequestId={RequestId} FailureType={FailureType}",id,ex.GetType().Name);
                if(ex is HttpRequestException { StatusCode:System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden })break;
            }
        }
    }
}

public static class GalleryExchangeEndpoints
{
    public static void MapGalleryExchange(this WebApplication app)
    {
        var group=app.MapGroup("/api/gallery-exchange").RequireAuthorization();
        group.MapGet("/requests",(Guid? jobId,ClaimsPrincipal user,GalleryExchangeStore store)=>Run(()=>Results.Ok(store.List(user,jobId))));
        group.MapGet("/jobs",(ClaimsPrincipal user,GalleryExchangeStore store)=>Run(()=>Results.Ok(store.Jobs(user))));
        group.MapGet("/requests/{id:guid}/activity",(Guid id,ClaimsPrincipal user,GalleryExchangeStore store)=>Run(()=>Results.Ok(store.Activity(id,user))));
        group.MapPost("/requests/{id:guid}/review",(Guid id,GalleryRequestReview request,ClaimsPrincipal user,GalleryExchangeStore store)=>Run(()=>Results.Ok(store.Review(id,request,user))));
        group.MapPost("/requests/{id:guid}/publish",(Guid id,GalleryTransferCommand request,ClaimsPrincipal user,GalleryExchangeStore store)=>Run(()=>Results.Ok(store.Publish(id,request,user))));
        group.MapPost("/requests/{id:guid}/revoke",(Guid id,GalleryRevocationCommand request,ClaimsPrincipal user,GalleryExchangeStore store)=>Run(()=>Results.Ok(store.Revoke(id,request.Reason,user))));
        group.MapPost("/requests/{id:guid}/retry",(Guid id,ClaimsPrincipal user,GalleryExchangeStore store)=>Run(()=>{store.Retry(id,user);return Results.Ok(new{state="Retry queued"});}));
    }
    static IResult Run(Func<IResult> action)
    {
        try{return action();}
        catch(UnauthorizedAccessException){return Results.Forbid();}
        catch(KeyNotFoundException){return Results.NotFound();}
        catch(ArgumentException ex){return Results.BadRequest(new{message=ex.Message});}
    }
}
