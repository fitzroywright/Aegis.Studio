using System.Text.Json;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Workflow;

namespace Aegis.Studio.Web.Intake;

public sealed class GallerySelectionStore(StudioDatabase database)
{
    public Guid Import(string source,JsonElement payload)
    {
        Guid id=payload.GetProperty("reference").GetGuid(),publication=payload.GetProperty("publicationId").GetGuid(),client=payload.GetProperty("clientId").GetGuid();
        var versions=payload.GetProperty("versionIds").EnumerateArray().Select(x=>x.GetGuid()).ToArray();
        if(id==Guid.Empty||publication==Guid.Empty||client==Guid.Empty||versions.Length is <1 or >10000||versions.Distinct().Count()!=versions.Length)throw new ArgumentException("Invalid gallery selection snapshot.");
        _=payload.GetProperty("submittedAt").GetDateTimeOffset();
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        WorkflowStore.Sql(c,tx,"select pg_advisory_xact_lock(hashtextextended($1,0))","gallery-selection:"+source+":"+id);
        using(var q=WorkflowStore.Command(c,tx,"select conversation_id,payload=$3::jsonb from studio_website_gallery_selections where source=$1 and external_id=$2",source,id,payload.GetRawText()))
        using(var r=q.ExecuteReader())if(r.Read())
        {
            if(!r.GetBoolean(1))throw new ArgumentException("Selection submission ID was reused with different content.");
            return r.GetGuid(0);
        }
        Guid conversation,job;string manifest;
        using(var q=WorkflowStore.Command(c,tx,"""
          select j.conversation_id,e.job_id,s.manifest::text from studio_gallery_exports e
          join studio_workflow_publications p on p.id=e.publication_id and p.job_id=e.job_id
          join studio_workflow_submissions s on s.id=p.submission_id
          join studio_jobs j on j.id=e.job_id
          where e.publication_id=$1 and e.client_id=$2 and j.client_id=$2 and j.conversation_id is not null
          """,publication,client))
        using(var r=q.ExecuteReader())
        {
            if(!r.Read())throw new ArgumentException("Selection publication does not belong to this Studio client.");
            conversation=r.GetGuid(0);job=r.GetGuid(1);manifest=r.GetString(2);
        }
        var allowed=JsonSerializer.Deserialize<JsonElement>(manifest).EnumerateArray().Select(x=>x.GetProperty("versionId").GetGuid()).ToHashSet();
        if(versions.Any(x=>!allowed.Contains(x)))throw new ArgumentException("Selected version is outside the pinned publication.");
        var names=new List<string>();
        foreach(var version in versions)
        {
            string name=WorkflowStore.Scalar(c,tx,"select file_name from studio_asset_versions where id=$1 and job_id=$2",version,job) as string??throw new ArgumentException("Selected photograph is unavailable.");
            names.Add($"{name} ({version:D})");
        }
        WorkflowStore.Sql(c,tx,"insert into studio_website_gallery_selections(source,external_id,publication_id,client_id,conversation_id,payload) values($1,$2,$3,$4,$5,$6::jsonb)",source,id,publication,client,conversation,payload.GetRawText());
        WorkflowStore.Sql(c,tx,"update studio_conversations set is_closed=false,job_id=$2 where id=$1",conversation,job);
        WorkflowStore.Sql(c,tx,"insert into studio_messages(id,conversation_id,author_display_name,visibility,body,created_at_utc,external_message_id) values($1,$2,'Client gallery','Internal',$3,now(),$4)",
            Guid.NewGuid(),conversation,$"Client submitted {versions.Length} photograph selection(s). Submission: {id:D}\n"+string.Join("\n",names),"gallery-selection:"+source+":"+id);
        tx.Commit();return conversation;
    }
}

// All network requests originate in Studio. The API retains each snapshot until acknowledged.
public sealed class GallerySelectionWorker(StudioDatabase database,IConfiguration configuration,ILogger<GallerySelectionWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if(!configuration.GetValue<bool>("PublicSite:SelectionPollingEnabled"))return;
        while(!ct.IsCancellationRequested)
        {
            try
            {
                using var site=new PublicSiteClient(configuration);
                await Synchronize(site,ct);
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception ex){logger.LogError("Gallery selection polling will retry. ErrorType={ErrorType}",ex.GetType().Name);}
            try{await Task.Delay(TimeSpan.FromSeconds(60),ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
        }
    }
    public async Task Synchronize(PublicSiteClient site,CancellationToken ct)
    {
        string source=new Uri(configuration["PublicSite:BaseUrl"]!).GetLeftPart(UriPartial.Authority);
        // A cursor is only a position within this scan, never a durable checkpoint:
        // imports and acknowledgements can fail independently anywhere in a page.
        string cursor="";
        var visited=new HashSet<string>(StringComparer.Ordinal){cursor};
        while(true)
        {
            ct.ThrowIfCancellationRequested();
            var feed=await site.Get("/api/v1/gallery-selections"+(cursor.Length==0?"":"?cursor="+Uri.EscapeDataString(cursor)),ct);
            bool hasMore=feed.GetProperty("hasMore").GetBoolean();
            string? next=hasMore?feed.GetProperty("nextCursor").GetString():null;
            if(hasMore&&(string.IsNullOrEmpty(next)||!visited.Add(next)))
                throw new InvalidOperationException("Selection feed cursor did not advance.");
            foreach(var payload in feed.GetProperty("items").EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    new GallerySelectionStore(database).Import(source,payload);
                    await site.Post($"/api/v1/gallery-selections/{payload.GetProperty("reference").GetGuid():D}/ack",new{},ct);
                }
                catch(Exception ex)when(ex is not OperationCanceledException||!ct.IsCancellationRequested)
                {logger.LogWarning("Gallery selection retained for retry. ErrorType={ErrorType}",ex.GetType().Name);}
            }
            if(!hasMore)break;
            cursor=next!;
        }

    }
}
