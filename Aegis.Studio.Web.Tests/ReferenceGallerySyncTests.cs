using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aegis.Studio.Web.Intake;
using Aegis.Studio.Web.Workflow;
using Common.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class ReferenceGallerySyncTests
{
    private static IConfiguration Config()=>new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
    {
        ["PublicSite:BaseUrl"]="https://reference.example.test/",
        ["PublicSite:IntegrationToken"]="test-only-scoped-key-at-least-32-characters"
    }).Build();

    [StudioPostgresFact]
    public async Task FailedUploadsAndTimeoutsPersistAndResumeWithoutDuplicateReviewConversations()
    {
        using var f=new GalleryFixture();var config=Config();var requests=new List<string>();bool uploaded=false,fail=true;
        using var site=new PublicSiteClient(config,new Handler(async request=>
        {
            Assert.Equal("reference.example.test",request.RequestUri!.Host);
            requests.Add(request.Method+" "+request.RequestUri.AbsolutePath);
            if(request.Method==HttpMethod.Post)return Json(new{storedVersionIds=uploaded?new[]{f.Version}:Array.Empty<Guid>()});
            uploaded=true;
            Assert.Equal(new byte[]{255,216,255,217},await request.Content!.ReadAsByteArrayAsync());
            if(fail)throw new TaskCanceledException("Lost upload response");
            return Json(new{stored=true});
        }));
        var worker=new GalleryPushWorker(f.Context.Database,new LocalFileStorage(f.Context.Root),config,NullLogger<GalleryPushWorker>.Instance);
        await worker.Synchronize(site,CancellationToken.None);
        Assert.Equal("Failed",f.Scalar("select state from studio_gallery_exports"));
        Assert.Equal("TaskCanceledException",f.Scalar("select last_error from studio_gallery_exports"));
        Assert.Equal(0L,f.Scalar("select count(*) from studio_conversations where channel='Website Gallery'"));
        fail=false;await worker.Synchronize(site,CancellationToken.None);await worker.Synchronize(site,CancellationToken.None);
        Assert.Equal("PendingReview",f.Scalar("select state from studio_gallery_exports"));
        Assert.Equal(DBNull.Value,f.Scalar("select last_error from studio_gallery_exports"));
        Assert.Equal(1L,f.Scalar("select count(*) from studio_conversations where channel='Website Gallery'"));
        Assert.Equal(1L,f.Scalar("select count(*) from studio_conversations where channel='Website Gallery' and job_id is null and not is_closed"));
        Assert.Equal(1L,f.Scalar("select count(*) from studio_messages where external_message_id like 'gallery-publication:%'"));
        Assert.Single(requests,x=>x.StartsWith("PUT "));
        Assert.Equal(2,requests.Count(x=>x=="POST /api/v1/galleries"));
    }

    [StudioPostgresFact]
    public async Task FailedReviewTransactionRollsBackConversationAndCanBeRetried()
    {
        using var f=new GalleryFixture();var config=Config();
        f.Sql("create function reject_review() returns trigger language plpgsql as $$ begin if new.state='PendingReview' then raise exception 'test failure'; end if; return new; end $$");
        f.Sql("create trigger reject_review before insert or update on studio_gallery_exports for each row execute function reject_review()");
        using var site=new PublicSiteClient(config,new Handler(_=>Task.FromResult(Json(new{storedVersionIds=new[]{f.Version}}))));
        var worker=new GalleryPushWorker(f.Context.Database,new LocalFileStorage(f.Context.Root),config,NullLogger<GalleryPushWorker>.Instance);
        await worker.Synchronize(site,CancellationToken.None);
        Assert.Equal("Failed",f.Scalar("select state from studio_gallery_exports"));
        Assert.Equal(0L,f.Scalar("select count(*) from studio_conversations where channel='Website Gallery'"));
        f.Sql("drop trigger reject_review on studio_gallery_exports");
        await worker.Synchronize(site,CancellationToken.None);
        Assert.Equal("PendingReview",f.Scalar("select state from studio_gallery_exports"));
        Assert.Equal(1L,f.Scalar("select count(*) from studio_conversations where channel='Website Gallery'"));
        // Recover a conversation left by the earlier nontransactional implementation.
        f.Sql("delete from studio_gallery_exports");
        await worker.Synchronize(site,CancellationToken.None);
        Assert.Equal(1L,f.Scalar("select count(*) from studio_conversations where channel='Website Gallery'"));
        Assert.Equal(1L,f.Scalar("select count(*) from studio_messages where external_message_id like 'gallery-publication:%'"));
    }

    [StudioPostgresFact]
    public async Task RevocationFailureKeepsStateAndDoesNotPreventOtherRevocations()
    {
        using var f=new GalleryFixture();f.Export("Published");Guid second=Guid.NewGuid();
        f.Sql("insert into studio_workflow_publications(id,job_id,submission_id,reference,provider,actor) select $1,job_id,submission_id,$1::text,provider,actor from studio_workflow_publications where id=$2",second,f.Publication);
        f.Sql("insert into studio_gallery_exports(publication_id,state,job_id,client_id) values($1,'PendingReview',$2,$3)",second,f.Context.Job,f.Client);
        f.Sql("update studio_workflow_submissions set status='Rejected'");
        bool fail=true;var attempted=new List<Guid>();var config=Config();
        using var site=new PublicSiteClient(config,new Handler(request=>
        {
            var id=Guid.Parse(request.RequestUri!.Segments[^2].Trim('/'));attempted.Add(id);
            if(id==f.Publication&&fail)throw new HttpRequestException("Unavailable");
            return Task.FromResult(Json(new{state="Revoked"}));
        }));
        var worker=new GalleryPushWorker(f.Context.Database,new LocalFileStorage(f.Context.Root),config,NullLogger<GalleryPushWorker>.Instance);
        await worker.Synchronize(site,CancellationToken.None);
        Assert.Equal("Published",f.Scalar("select state from studio_gallery_exports where publication_id=$1",f.Publication));
        Assert.Equal("Revoked",f.Scalar("select state from studio_gallery_exports where publication_id=$1",second));
        Assert.Equal(2,attempted.Count);fail=false;await worker.Synchronize(site,CancellationToken.None);
        Assert.Equal("Revoked",f.Scalar("select state from studio_gallery_exports where publication_id=$1",f.Publication));
    }

    [StudioPostgresFact]
    public async Task LostSelectionAcknowledgementReplaysWithoutDuplicateMessagesAndInvalidItemsAreNotAcknowledged()
    {
        using var f=new GalleryFixture();f.Export("Published");Guid valid=Guid.NewGuid(),invalid=Guid.NewGuid();
        var payload=f.Selection(valid);var wrong=f.Selection(invalid,Guid.NewGuid());
        int acknowledgements=0;bool fail=true;var config=Config();
        using var site=new PublicSiteClient(config,new Handler(request=>
        {
            if(request.Method==HttpMethod.Get)
            {
                if(string.IsNullOrEmpty(request.RequestUri!.Query))return Task.FromResult(Json(new{items=new[]{wrong},hasMore=true,nextCursor="page2"}));
                Assert.Equal("?cursor=page2",request.RequestUri.Query);
                return Task.FromResult(Json(new{items=new[]{payload},hasMore=false,nextCursor="done"}));
            }
            Assert.Contains(valid.ToString("D"),request.RequestUri!.AbsolutePath);acknowledgements++;
            if(fail)throw new HttpRequestException("Lost acknowledgement");
            return Task.FromResult(Json(new{importStatus="Imported"}));
        }));
        var worker=new GallerySelectionWorker(f.Context.Database,config,NullLogger<GallerySelectionWorker>.Instance);
        await worker.Synchronize(site,CancellationToken.None);
        Assert.Equal(1L,f.Scalar("select count(*) from studio_website_gallery_selections"));
        fail=false;await worker.Synchronize(site,CancellationToken.None);
        Assert.Equal(2,acknowledgements);
        Assert.Equal(1L,f.Scalar("select count(*) from studio_messages where external_message_id like 'gallery-selection:%'"));
        Assert.Equal(false,f.Scalar("select is_closed from studio_conversations where id=$1",f.JobConversation));
        Assert.Contains("Portrait.jpg",(string)f.Scalar("select body from studio_messages where external_message_id like 'gallery-selection:%'"));
        Assert.Equal(f.JobConversation,f.Scalar("select conversation_id from studio_messages where external_message_id like 'gallery-selection:%'"));
        var store=new GallerySelectionStore(f.Context.Database);
        Assert.Throws<ArgumentException>(()=>store.Import("https://reference.example.test",f.Selection(valid,Guid.NewGuid())));
        Assert.Throws<ArgumentException>(()=>store.Import("https://reference.example.test",f.Selection(Guid.NewGuid(),client:Guid.NewGuid())));
        // Historical snapshots remain valid after revocation; they were accepted before it.
        f.Sql("update studio_gallery_exports set state='Revoked'");
        Assert.Equal(f.JobConversation,store.Import("https://reference.example.test",payload));
    }

    [StudioPostgresFact]
    public async Task PartialPageFailuresDoNotStarveLaterPagesAndRestartRetriesPendingSelections()
    {
        using var f=new GalleryFixture();f.Export("Published");var config=Config();
        var selections=Enumerable.Range(0,102).Select(_=>f.Selection(Guid.NewGuid())).ToArray();
        var ids=selections.Select(x=>x.GetProperty("reference").GetGuid()).ToArray();
        var acknowledged=new HashSet<Guid>();var paths=new List<string>();var attempts=new List<Guid>();
        bool fail=true;
        // The first import fails transactionally; the second import commits but
        // loses its acknowledgement response. Other items must still progress.
        f.Sql($"create function reject_first_selection() returns trigger language plpgsql as $$ begin if new.external_id='{ids[0]:D}'::uuid then raise exception 'temporary import failure'; end if; return new; end $$");
        f.Sql("create trigger reject_first_selection before insert on studio_website_gallery_selections for each row execute function reject_first_selection()");
        using var site=new PublicSiteClient(config,new Handler(request=>
        {
            Assert.Equal("reference.example.test",request.RequestUri!.Host);
            if(request.Method==HttpMethod.Get)
            {
                paths.Add(request.RequestUri.PathAndQuery);
                int after=string.IsNullOrEmpty(request.RequestUri.Query)?0:int.Parse(request.RequestUri.Query[8..]);
                var pending=Enumerable.Range(after,selections.Length-after).Where(i=>!acknowledged.Contains(ids[i])).Take(51).ToArray();
                var page=pending.Take(50).ToArray();
                return Task.FromResult(Json(new{items=page.Select(i=>selections[i]),hasMore=pending.Length>50,nextCursor=(page.LastOrDefault()+1).ToString()}));
            }
            Assert.Equal(HttpMethod.Post,request.Method);
            var id=Guid.Parse(request.RequestUri.Segments[^2].Trim('/'));attempts.Add(id);
            if(fail&&id==ids[1])throw new HttpRequestException("Acknowledgement request interrupted");
            acknowledged.Add(id);return Task.FromResult(Json(new{importStatus="Imported"}));
        }));
        GallerySelectionWorker Worker()=>new(f.Context.Database,config,NullLogger<GallerySelectionWorker>.Instance);
        await Worker().Synchronize(site,CancellationToken.None);
        Assert.Equal(new[]{"/api/v1/gallery-selections","/api/v1/gallery-selections?cursor=50","/api/v1/gallery-selections?cursor=100"},paths);
        Assert.DoesNotContain(ids[0],attempts);Assert.Contains(ids[101],acknowledged);
        Assert.Equal(100,acknowledged.Count);
        Assert.Equal(101L,f.Scalar("select count(*) from studio_website_gallery_selections"));
        f.Sql("drop trigger reject_first_selection on studio_website_gallery_selections");
        fail=false;paths.Clear();
        await Worker().Synchronize(site,CancellationToken.None);
        await Worker().Synchronize(site,CancellationToken.None);
        Assert.All(paths,path=>Assert.Equal("/api/v1/gallery-selections",path));
        Assert.Equal(102,acknowledged.Count);Assert.Equal(2,attempts.Count(x=>x==ids[1]));
        Assert.Equal(102L,f.Scalar("select count(*) from studio_website_gallery_selections"));
        Assert.Equal(102L,f.Scalar("select count(*) from studio_messages where external_message_id like 'gallery-selection:%'"));
    }

    [StudioPostgresFact]
    public async Task InterruptedPageFetchRestartsAtBeginningAndReplaysCommittedImport()
    {
        using var f=new GalleryFixture();f.Export("Published");var config=Config();
        var first=f.Selection(Guid.NewGuid());var second=f.Selection(Guid.NewGuid());
        bool interrupted=true;int firstPageFetches=0,acks=0;
        using var site=new PublicSiteClient(config,new Handler(request=>
        {
            Assert.Equal("reference.example.test",request.RequestUri!.Host);
            if(request.Method==HttpMethod.Post)
            {
                acks++;
                if(interrupted)throw new TaskCanceledException("Lost acknowledgement response");
                return Task.FromResult(Json(new{importStatus="Imported"}));
            }
            if(request.RequestUri.Query.Length==0)
            {
                firstPageFetches++;
                return Task.FromResult(Json(new{items=new[]{first},hasMore=true,nextCursor="second"}));
            }
            Assert.Equal("?cursor=second",request.RequestUri.Query);
            if(interrupted)throw new HttpRequestException("Feed connection interrupted");
            return Task.FromResult(Json(new{items=new[]{second},hasMore=false}));
        }));
        GallerySelectionWorker Worker()=>new(f.Context.Database,config,NullLogger<GallerySelectionWorker>.Instance);
        await Assert.ThrowsAsync<HttpRequestException>(()=>Worker().Synchronize(site,CancellationToken.None));
        Assert.Equal(1L,f.Scalar("select count(*) from studio_website_gallery_selections"));
        interrupted=false;await Worker().Synchronize(site,CancellationToken.None);
        Assert.Equal(2,firstPageFetches);Assert.Equal(3,acks);
        Assert.Equal(2L,f.Scalar("select count(*) from studio_messages where external_message_id like 'gallery-selection:%'"));
    }

    [StudioPostgresFact]
    public async Task CyclicPaginationFailsAndCancelledScanDoesNotImportAnotherItem()
    {
        using var f=new GalleryFixture();f.Export("Published");var config=Config();int pages=0;
        using(var site=new PublicSiteClient(config,new Handler(_=>
        {
            if(++pages>3)throw new HttpRequestException("Cursor loop was not detected");
            return Task.FromResult(Json(new{items=Array.Empty<object>(),hasMore=true,nextCursor=pages==2?"b":"a"}));
        })))
            await Assert.ThrowsAsync<InvalidOperationException>(()=>new GallerySelectionWorker(f.Context.Database,config,NullLogger<GallerySelectionWorker>.Instance).Synchronize(site,CancellationToken.None));
        Assert.Equal(3,pages);
        using var cancellation=new CancellationTokenSource();
        using var cancelledSite=new PublicSiteClient(config,new Handler(_=>
        {
            cancellation.Cancel();
            return Task.FromResult(Json(new{items=new[]{f.Selection(Guid.NewGuid())},hasMore=false}));
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new GallerySelectionWorker(f.Context.Database,config,NullLogger<GallerySelectionWorker>.Instance).Synchronize(cancelledSite,cancellation.Token));
        Assert.Equal(0L,f.Scalar("select count(*) from studio_website_gallery_selections"));
    }

    private static HttpResponseMessage Json(object value)=>new(HttpStatusCode.OK){Content=JsonContent.Create(value)};

    [StudioPostgresFact]
    public void FrontDeskCannotRepublishRevokedUnapprovedOrReassignedGalleries()
    {
        using var f=new GalleryFixture();f.Export("PendingReview");
        Assert.True(GalleryPushWorker.CanPublish(f.Context.Database,f.Publication));
        f.Sql("update studio_gallery_exports set state='Revoked'");Assert.False(GalleryPushWorker.CanPublish(f.Context.Database,f.Publication));
        f.Sql("update studio_gallery_exports set state='Published'");f.Sql("update studio_workflow_submissions set status='Rejected'");
        Assert.False(GalleryPushWorker.CanPublish(f.Context.Database,f.Publication));
        f.Sql("update studio_workflow_submissions set status='Approved'");f.Sql("update studio_jobs set client_id=null");
        Assert.False(GalleryPushWorker.CanPublish(f.Context.Database,f.Publication));
    }

    [StudioPostgresFact]
    public void SelectionSnapshotAndStaffMessageRollBackTogether()
    {
        using var f=new GalleryFixture();f.Export("Published");var store=new GallerySelectionStore(f.Context.Database);var payload=f.Selection(Guid.NewGuid());
        f.Sql("create function reject_selection_message() returns trigger language plpgsql as $$ begin raise exception 'test failure'; end $$");
        f.Sql("create trigger reject_selection_message before insert on studio_messages for each row execute function reject_selection_message()");
        Assert.Throws<PostgresException>(()=>store.Import("https://reference.example.test",payload));
        Assert.Equal(0L,f.Scalar("select count(*) from studio_website_gallery_selections"));
        Assert.Equal(true,f.Scalar("select is_closed from studio_conversations where id=$1",f.JobConversation));
        f.Sql("drop trigger reject_selection_message on studio_messages");
        Assert.Equal(f.JobConversation,store.Import("https://reference.example.test",payload));
        Assert.Equal(1L,f.Scalar("select count(*) from studio_website_gallery_selections"));
    }

    [StudioPostgresFact]
    public async Task RepeatedExportFailuresDoNotStarveLaterPublications()
    {
        using var f=new GalleryFixture();
        for(int i=0;i<10;i++)f.Sql("insert into studio_workflow_publications(id,job_id,submission_id,reference,provider,actor) select $1,job_id,submission_id,$1::text,provider,actor from studio_workflow_publications where id=$2",Guid.NewGuid(),f.Publication);
        var attempted=new HashSet<Guid>();var config=Config();
        using var site=new PublicSiteClient(config,new Handler(async request=>
        {
            var body=await request.Content!.ReadFromJsonAsync<JsonElement>();attempted.Add(body.GetProperty("id").GetGuid());throw new HttpRequestException("Unavailable");
        }));
        var worker=new GalleryPushWorker(f.Context.Database,new LocalFileStorage(f.Context.Root),config,NullLogger<GalleryPushWorker>.Instance);
        await worker.Synchronize(site,CancellationToken.None);Assert.Equal(10,attempted.Count);
        await worker.Synchronize(site,CancellationToken.None);Assert.Equal(11,attempted.Count);
        Assert.Equal(11L,f.Scalar("select count(*) from studio_gallery_exports where state='Failed'"));
    }
    private sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> send):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>send(request);
    }
    private sealed class GalleryFixture:IDisposable
    {
        public IntegratedWorkflowTests.WorkflowContext Context{get;}=new();
        public Guid Client{get;}=Guid.NewGuid();
        public Guid Publication{get;}=Guid.NewGuid();
        public Guid Version{get;}=Guid.NewGuid();
        public Guid Conversation{get;}=Guid.NewGuid();
        public Guid JobConversation{get;}
        public GalleryFixture()
        {
            JobConversation=(Guid)Scalar("select conversation_id from studio_jobs where id=$1",Context.Job);
            WorkflowSchema.Ensure(Context.Database);
            Sql("set studio.demo_seed='true'");
            Sql("alter table studio_conversations add column if not exists client_id uuid null references studio_clients(id)");
            // Apply the actual additive gallery migration twice, including upgrade compatibility.
            using var stream=typeof(IntakeSchema).Assembly.GetManifestResourceStream("Aegis.Studio.Web.Intake.IntakeSchema.sql")!;
            using var reader=new StreamReader(stream);string sql=reader.ReadToEnd();sql=sql[sql.IndexOf("create table if not exists studio_gallery_exports(",StringComparison.Ordinal)..];Sql(sql);Sql(sql);
            Sql("insert into studio_clients(id,display_name,email) values($1,'Client','test@example.test')",Client);
            Sql("update studio_jobs set client_id=$2,status='Ready for Delivery' where id=$1",Context.Job,Client);
            Sql("insert into studio_asset_versions(id,job_id,asset_key,version_number,file_name,content_type,size_bytes,sha256,stored_path,created_by,created_at_utc) values($1,$2,'portrait',1,'Portrait.jpg','image/jpeg',4,'test','portrait.jpg','Test',now())",Version,Context.Job);
            Guid submission=Guid.NewGuid();
            Sql("insert into studio_workflow_submissions(id,job_id,kind,revision,manifest,submitted_by,status) values($1,$2,'Finished',1,$3::jsonb,'Test','Approved')",submission,Context.Job,JsonSerializer.Serialize(new[]{new{versionId=Version}}));
            Sql("insert into studio_workflow_publications(id,job_id,submission_id,reference,provider,actor) values($1,$2,$3,$1::text,'PublicSite','Test')",Publication,Context.Job,submission);
            Directory.CreateDirectory(Path.Combine(Context.Root,"VersionStore"));File.WriteAllBytes(Path.Combine(Context.Root,"VersionStore","portrait.jpg"),[255,216,255,217]);
        }
        public void Export(string state)
        {
            Sql("update studio_conversations set is_closed=true where id=$1",JobConversation);
            Sql("insert into studio_conversations(id,workspace_id,subject,contact_name,contact_address,channel,is_closed,created_at_utc,client_id) values($1,$2,'Gallery','Client','test@example.test','Website Gallery',true,now(),$3)",Conversation,Context.Workspace,Client);
            Sql("insert into studio_gallery_exports(publication_id,state,conversation_id,job_id,client_id) values($1,$2,$3,$4,$5)",Publication,state,Conversation,Context.Job,Client);
        }
        public JsonElement Selection(Guid id,Guid? version=null,Guid? client=null)=>JsonSerializer.SerializeToElement(new{reference=id,publicationId=Publication,clientId=client??Client,versionIds=new[]{version??Version},submittedAt="2026-10-24T12:00:00Z",importStatus="Pending"});
        public void Sql(string sql,params object[] values){using var q=Command(sql,values);q.ExecuteNonQuery();}
        public object Scalar(string sql,params object[] values){using var q=Command(sql,values);return q.ExecuteScalar()!;}
        private NpgsqlCommand Command(string sql,object[] values){var q=new NpgsqlCommand(sql,Context.Connection);foreach(var value in values)q.Parameters.AddWithValue(value);return q;}
        public void Dispose()=>Context.Dispose();
    }
}
