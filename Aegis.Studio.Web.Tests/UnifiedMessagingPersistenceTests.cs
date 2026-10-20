using System.Security.Claims;
using System.Text;
using Aegis.Studio.Application.EmailRouting;
using Aegis.Studio.Web.Email;
using Aegis.Studio.Web.Messaging;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Profiles;
using Common.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Npgsql;
using Xunit;

namespace Aegis.Studio.Web.Tests;

// Opt in with STUDIO_TEST_POSTGRES. Each fixture owns a random schema and a temporary storage root.
[Collection("Studio email database")]
public sealed class UnifiedMessagingPersistenceTests
{
    [StudioPostgresFact]
    public async Task InternalSendAndRequestRetryProduceOnePrivateMessageAndNoExternalDelivery()
    {
        using var f=await Fixture.Create();
        var tamara=f.Book.GetAll().Single(e=>e.UserName=="tamara");
        var request=new UnifiedSendRequest(Guid.NewGuid(),null,"Shoot handoff","Ready for production",[tamara.Id]);
        var first=await f.Router.SendAsync(request,new FormFileCollection(),f.Paul,default);
        var repeated=await f.Router.SendAsync(request,new FormFileCollection(),f.Paul,default);
        Assert.Equal(first.MessageId,repeated.MessageId);Assert.Equal(first.ThreadId,repeated.ThreadId);
        Assert.Single(f.Messages.GetMessages(first.ThreadId,f.Tamara));Assert.Equal(0,f.Count("studio_outbound_email"));Assert.Equal(0,f.Directory.Calls);
        Assert.Throws<UnauthorizedAccessException>(()=>f.Messages.GetMessages(first.ThreadId,f.Other));
        Assert.Throws<UnauthorizedAccessException>(()=>f.Messages.GetMessages(first.ThreadId,f.Administrator));
        Assert.Equal("Delivered",Assert.Single(f.Router.GetDeliveries(first.ThreadId,f.Tamara)).Status);
    }
    [StudioPostgresFact]
    public async Task ClientExchangePreservesSenderAttachmentsCorrelationAndInboundIdempotency()
    {
        using var f=await Fixture.Create();f.VerifyPaul();
        var client=f.Book.GetOrCreateExternal("Client","client@outside.test");
        var files=Files("brief.txt","Shoot details");
        var sent=await f.Router.SendAsync(new(Guid.NewGuid(),null,"Client booking","Here is the brief",[client.Id]),files,f.Paul,default);
        var outbound=Assert.Single(f.Outbox.GetPending());
        Assert.Equal("paul@studio.test",outbound.FromAddress);Assert.Equal("router@studio.test",outbound.SenderMailbox);
        Assert.NotNull(outbound.RfcMessageId);Assert.Single(f.Attachments.GetForThread(sent.ThreadId));
        var lease=Guid.NewGuid();var claimed=f.Outbox.ClaimNext(["Email"],lease)!;f.Outbox.RecordVerifiedInternetId(claimed.Id,lease,new("fake-provider-id",claimed.RfcMessageId!));f.Outbox.MarkProviderAccepted(claimed.Id,"fake-provider-id",lease);
        var reply=new InboundEmail("<reply@outside.test>",null,"Client","client@outside.test",["router@studio.test"],[],
            new Dictionary<string,string>{{"In-Reply-To",outbound.RfcMessageId!}},"Re: Client booking","Thank you",DateTimeOffset.UtcNow,
            [new("confirmation.txt","text/plain",Encoding.UTF8.GetBytes("Confirmed"))]);
        Assert.True((await f.Intake.ReceiveAsync(reply,default)).Routed);Assert.True((await f.Intake.ReceiveAsync(reply,default)).Routed);
        Assert.Equal(2,f.Messages.GetMessages(sent.ThreadId,f.Paul).Count);Assert.Equal(1,f.Count("studio_inbound_receipts"));
        Assert.Equal(2,f.Attachments.GetForThread(sent.ThreadId).Count);Assert.Single(f.Book.GetAll(), e=>e.EmailAddress=="client@outside.test");
        Assert.Throws<UnauthorizedAccessException>(()=>f.Router.GetDeliveries(sent.ThreadId,f.Tamara));
    }
    [StudioPostgresFact]
    public async Task UnknownSendOutcomeIsHeldAndRequiresExplicitReconciliationBeforeRetry()
    {
        using var f=await Fixture.Create();f.VerifyPaul();var client=f.Book.GetOrCreateExternal("Client","client@outside.test");
        var sent=await f.Router.SendAsync(new(Guid.NewGuid(),null,"Booking","Details",[client.Id]),new FormFileCollection(),f.Paul,default);
        Guid lease=Guid.NewGuid();var outbound=f.Outbox.ClaimNext(["Email"],lease)!;
        f.Outbox.MarkAttention(outbound.Id,lease,"ProviderOutcomeUncertain: lost response");
        Assert.Throws<InvalidOperationException>(()=>f.Router.RetryDelivery(outbound.Id,f.Paul,false));
        Assert.Null(f.Outbox.ClaimNext(["Email"],Guid.NewGuid()));
        f.Router.RetryDelivery(outbound.Id,f.Paul,true);
        Assert.Equal(outbound.Id,f.Outbox.ClaimNext(["Email"],Guid.NewGuid())!.Id);
        Assert.Single(f.Router.GetDeliveries(sent.ThreadId,f.Paul));
    }
    [StudioPostgresFact]
    public async Task KnownProviderRejectionCanBeRetriedWithoutUncertainOutcomeAttestation()
    {
        using var f=await Fixture.Create();f.VerifyPaul();var client=f.Book.GetOrCreateExternal("Client","client@outside.test");
        await f.Router.SendAsync(new(Guid.NewGuid(),null,"Booking","Details",[client.Id]),new FormFileCollection(),f.Paul,default);
        Guid lease=Guid.NewGuid();var outbound=f.Outbox.ClaimNext(["Email"],lease)!;
        f.Outbox.MarkAttention(outbound.Id,lease,"ProviderRejected:403");
        f.Router.RetryDelivery(outbound.Id,f.Paul,false);
        Assert.Equal(outbound.Id,f.Outbox.ClaimNext(["Email"],Guid.NewGuid())!.Id);
    }
    [StudioPostgresFact]
    public async Task ExpiredSendingLeaseCannotAutomaticallySendASecondCopy()
    {
        using var f=await Fixture.Create();f.VerifyPaul();var client=f.Book.GetOrCreateExternal("Client","client@outside.test");
        await f.Router.SendAsync(new(Guid.NewGuid(),null,"Booking","Details",[client.Id]),new FormFileCollection(),f.Paul,default);
        var claimed=f.Outbox.ClaimNext(["Email"],Guid.NewGuid())!;
        using(var c=f.Source.OpenConnection())using(var q=new NpgsqlCommand("update studio_outbound_email set lease_until_utc=now()-interval '1 minute' where id=$1",c)){q.Parameters.AddWithValue(claimed.Id);q.ExecuteNonQuery();}
        Assert.Null(f.Outbox.ClaimNext(["Email"],Guid.NewGuid()));
        using var c2=f.Source.OpenConnection();using var q2=new NpgsqlCommand("select status from studio_outbound_email where id=$1",c2);q2.Parameters.AddWithValue(claimed.Id);Assert.Equal("Attention",q2.ExecuteScalar());
    }
    [StudioPostgresFact]
    public async Task UnverifiedSenderRetainsInternalHistoryAndAVisibleRecoverableFailure()
    {
        using var f=await Fixture.Create();var client=f.Book.GetOrCreateExternal("Client","client@outside.test");
        var sent=await f.Router.SendAsync(new(Guid.NewGuid(),null,"Booking","Details",[client.Id]),new FormFileCollection(),f.Paul,default);
        Assert.Equal("Attention",sent.Status);Assert.Equal(0,f.Count("studio_outbound_email"));
        Assert.Single(f.Router.GetDeliveries(sent.ThreadId,f.Paul));f.VerifyPaul();
        var retry=await f.Router.RetryRoutingAsync(sent.MessageId,f.Paul,default);Assert.Equal("Queued",retry.Status);Assert.Equal(1,f.Count("studio_outbound_email"));
        await f.Router.RetryRoutingAsync(sent.MessageId,f.Paul,default);Assert.Equal(1,f.Count("studio_outbound_email"));
    }
    [StudioPostgresFact]
    public async Task InternalMessageCannotReuseARequestIdWithDifferentBody()
    {
        using var f=await Fixture.Create();var tamara=f.Book.GetAll().Single(e=>e.UserName=="tamara");
        var request=new UnifiedSendRequest(Guid.NewGuid(),null,"Handoff","One",[tamara.Id]);
        await f.Router.SendAsync(request,new FormFileCollection(),f.Paul,default);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Router.SendAsync(request with{Body="Two"},new FormFileCollection(),f.Paul,default));
        Assert.Equal(1,f.Count("studio_inapp_messages"));
    }
    [StudioPostgresFact]
    public async Task PersonalInboxKeepsProviderRepliesInOnePrivateThread()
    {
        using var f=await Fixture.Create();
        var paul=f.Book.GetAll().Single(e=>e.UserName=="paul");
        f.Book.Save(paul with { EmailAddress="paul@studio.test", InboxType="Microsoft365", StudioInboxId="paul" });
        var first=new InboundEmail("<first@outside.test>","provider-thread-42","Client","client@outside.test",
            ["paul@studio.test"],[],new Dictionary<string,string>(),"Project details","Initial message",DateTimeOffset.UtcNow,[]);
        var second=first with { MessageId="<second@outside.test>", Body="Follow-up", ReceivedAt=DateTimeOffset.UtcNow.AddMinutes(1) };

        Assert.True((await f.Intake.ReceiveAsync(first,default)).Routed);
        Assert.True((await f.Intake.ReceiveAsync(second,default)).Routed);

        using var c=f.Source.OpenConnection();
        using var q=new NpgsqlCommand("""
            select count(distinct thread_id),count(*)
            from studio_inapp_messages
            where external_message_id=any($1)
            """,c);
        q.Parameters.AddWithValue(new[] { first.MessageId, second.MessageId });
        using var r=q.ExecuteReader();Assert.True(r.Read());
        Assert.Equal(1L,r.GetInt64(0));
        Assert.Equal(2L,r.GetInt64(1));
    }

    [StudioPostgresFact]
    public async Task SharedIncomingReadIsFirstReaderWinsAndNewRepliesReturnToUnread()
    {
        using var f=await Fixture.Create();
        var db=new StudioDatabase(f.Source);
        var workspace=new WorkspaceStore(db).Create(new("Weddings","weddings@studio.test","weddings",true,1));
        var route=new EmailRouteResult(true,workspace.Id,workspace.Name,workspace.EmailAddress,null);
        var firstEmail=new InboundEmail("<first@outside.test>",null,"Client","client@outside.test",
            ["weddings@studio.test"],[],new Dictionary<string,string>(),"Booking","Initial message",DateTimeOffset.UtcNow,[]);
        var conversation=f.Studio.ReceiveEmail(firstEmail,route).Conversation;
        Guid messageId;
        using(var c=f.Source.OpenConnection())
        using(var q=new NpgsqlCommand("select id from studio_messages where conversation_id=$1 and external_message_id=$2",c))
        {q.Parameters.AddWithValue(conversation.Id);q.Parameters.AddWithValue(firstEmail.MessageId);messageId=(Guid)q.ExecuteScalar()!;}
        var inbox=new StudioUnifiedInbox(db,new WorkspaceAccessService(db),new JobAccessService(db));
        ClaimsPrincipal Reader(string name)=>new(new ClaimsIdentity([
            new Claim("login_name",name),new Claim(ClaimTypes.Name,name),new Claim(ClaimTypes.Role,"Administrator")],"test"));
        var readerA=Reader("staff-a");var readerB=Reader("staff-b");

        Assert.Empty(inbox.Get(f.Other,0,100,null,null).Items);
        Assert.Throws<UnauthorizedAccessException>(()=>inbox.MarkIncomingDisplayed(conversation.Id,messageId,f.Other));
        Assert.True(Assert.Single(inbox.Get(readerA,0,100,null,null).Items).IsUnread);
        Assert.Single(inbox.Get(readerA,0,100,null,null,unreadOnly:true).Items);
        var receipts=await Task.WhenAll(
            Task.Run(()=>inbox.MarkIncomingDisplayed(conversation.Id,messageId,readerA)),
            Task.Run(()=>inbox.MarkIncomingDisplayed(conversation.Id,messageId,readerB)));
        Assert.Equal(receipts[0],receipts[1]);
        Assert.Empty(inbox.Get(readerB,0,100,null,null,unreadOnly:true).Items);
        Assert.False(Assert.Single(inbox.Get(readerB,0,100,null,null).Items).IsUnread);

        var reply=firstEmail with {MessageId="<second@outside.test>",Subject="Re: Booking",Body="A new question",ReceivedAt=DateTimeOffset.UtcNow.AddSeconds(1)};
        f.Studio.ReceiveEmail(reply,route,conversation.Id);
        Assert.Single(inbox.Get(readerB,0,100,null,null,unreadOnly:true).Items);
    }

    private static FormFileCollection Files(string name,string content)
    {
        var stream=new MemoryStream(Encoding.UTF8.GetBytes(content));return [new FormFile(stream,0,stream.Length,"files",name){Headers=new HeaderDictionary(),ContentType="text/plain"}];
    }
    internal sealed class Fixture:IDisposable
    {
        private readonly string schema="studio_messaging_test_"+Guid.NewGuid().ToString("N");
        private readonly string root=Path.Combine(Path.GetTempPath(),"studio-messaging-tests",Guid.NewGuid().ToString("N"));
        private readonly string originalConnection=System.Environment.GetEnvironmentVariable("STUDIO_TEST_POSTGRES")!;
        private ServiceProvider services=null!;
        public StudioEmailIntake Intake {get;private set;}=null!;
        public StudioInquiryAssociations Associations {get;private set;}=null!;
        public StudioStore Studio {get;private set;}=null!;
        public StudioEmailRouting Routing {get;private set;}=null!;
        public NpgsqlDataSource Source {get;private set;}=null!;
        public StudioAddressBook Book {get;private set;}=null!;public StudioUnifiedMessaging Router {get;private set;}=null!;
        public StudioInAppMessageStore Messages {get;private set;}=null!;public StudioMessageAttachmentStore Attachments {get;private set;}=null!;
        public StudioUnifiedInbound Inbound {get;private set;}=null!;public OutboundEmailStore Outbox {get;private set;}=null!;
        public UnifiedMessagingTests.Directory Directory {get;}=new();
        public ClaimsPrincipal Paul {get;}=User("paul");public ClaimsPrincipal Tamara {get;}=User("tamara");public ClaimsPrincipal Other {get;}=User("other");
        public ClaimsPrincipal Administrator {get;}=new(new ClaimsIdentity([new Claim("login_name","administrator"),new Claim(ClaimTypes.Role,"Administrator")],"test"));
        public static async Task<Fixture> Create(bool interruptAttachment=false)
        {
            var f=new Fixture();
            using(var c=new NpgsqlConnection(f.originalConnection)){await c.OpenAsync();using var q=new NpgsqlCommand($"create schema {f.schema}",c);await q.ExecuteNonQueryAsync();}
            var builder=new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(f.originalConnection){SearchPath=f.schema}.ConnectionString);
            builder.UsePhysicalConnectionInitializer(c=>{using var q=new NpgsqlCommand($"set search_path to {f.schema}",c);q.ExecuteNonQuery();},async c=>{using var q=new NpgsqlCommand($"set search_path to {f.schema}",c);await q.ExecuteNonQueryAsync();});
            f.Source=builder.Build();
            var db=new StudioDatabase(f.Source);await db.InitializeAsync();await new StudioFeatureDatabase(f.Source).InitializeAsync();await new StudioMessagingDatabase(f.Source).InitializeAsync();
            using(var c=f.Source.OpenConnection())using(var q=new NpgsqlCommand("""
                create table if not exists studio_clients(id uuid primary key,display_name text not null,email text,phone text,updated_at_utc timestamptz not null default now());
                create table if not exists studio_photographers(user_name text,display_name text,is_active boolean);
                create table if not exists studio_global_operational_roles(id uuid,name text,is_active boolean);
                create table if not exists studio_global_operational_role_members(role_id uuid,user_name text,display_name text,is_active boolean);
                """,c))q.ExecuteNonQuery();
            var employees=new StudioEmployeeDirectoryStore(db);employees.EnsureSchema();
            var profiles=new UserProfileStore(db);profiles.GetOrCreate(f.Paul);profiles.GetOrCreate(f.Tamara);
            var workspaces=new WorkspaceStore(db);var access=new WorkspaceAccessService(db);var jobs=new JobAccessService(db);
            var studio=new StudioStore(db,workspaces);
            f.Messages=new(db,profiles,access,jobs,studio);
            var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{
                ["Messaging:MicrosoftGraph:Email:Enabled"]="true",["Messaging:MicrosoftGraph:Email:SenderUpn"]="router@studio.test"}).Build();
            f.Attachments=new(new Environment(f.root),config,db,new LocalFileStorage(f.root));f.Attachments.EnsureSchema();
            f.Book=new(db,profiles,workspaces,access,jobs,employees);
            f.Router=new(db,f.Book,f.Messages,new(f.Directory,config),profiles,f.Attachments,config,access,jobs);f.Router.EnsureSchema();
            f.Outbox=new(db,workspaces);f.services=new ServiceCollection().AddLogging().AddSignalR().Services.BuildServiceProvider();
            f.Inbound=new(db,f.Book,f.Attachments,config,f.services.GetRequiredService<IHubContext<StudioMessageHub>>(),f.Messages);
            new Aegis.Studio.Web.FrontDesk.StudioClientStore(db).Migrate();
            new Aegis.Studio.Web.WorkspaceJobs.WorkspaceJobStore(db).EnsureSchema();
            f.Associations=new(db,studio,access,jobs);f.Associations.EnsureSchema();
            f.Studio=studio;f.Routing=new(db,new ConfiguredEmailRouter(workspaces));
            f.Intake=new(db,f.Routing,studio,f.Inbound,new(db),new Aegis.Studio.Web.Storage.AttachmentStore(new Environment(f.root),config,db,interruptAttachment?new InterruptStorage(new LocalFileStorage(f.root)):new LocalFileStorage(f.root)),f.Associations);
            // Queue tests do not opt into reminder scheduling.
            using(var c=f.Source.OpenConnection())using(var q=new NpgsqlCommand("create or replace function studio_client_reminder_dispatchable(uuid) returns boolean language sql as 'select false'",c))q.ExecuteNonQuery();
            return f;
        }
        public void VerifyPaul(){var paul=Book.GetAll().Single(e=>e.UserName=="paul");Book.Save(paul with{EmailAddress="paul@studio.test",SenderMailbox="router@studio.test",SenderVerified=true,VerificationEvidence="Fake-provider unit fixture only; no live-tenant claim."});}
        public long Count(string table){using var c=Source.OpenConnection();using var q=new NpgsqlCommand("select count(*) from "+table,c);return (long)q.ExecuteScalar()!;}
        public void Dispose(){services?.Dispose();Source?.Dispose();using var c=new NpgsqlConnection(originalConnection);c.Open();using var q=new NpgsqlCommand($"drop schema if exists {schema} cascade",c);q.ExecuteNonQuery();if(System.IO.Directory.Exists(root))System.IO.Directory.Delete(root,true);}
        private static ClaimsPrincipal User(string name)=>new(new ClaimsIdentity([new Claim("login_name",name),new Claim(ClaimTypes.Name,name),new Claim(ClaimTypes.Email,name+"@studio.test")],"test"));
        private sealed class InterruptStorage(IFileStorage inner):IFileStorage
        {
            private int writes;
            public Task<StoredFile> StoreAsync(StorageWriteRequest request,CancellationToken token=default)
            {if(Interlocked.Increment(ref writes)==2)throw new StorageException("TEST_INTERRUPTED","Simulated interrupted second attachment");return inner.StoreAsync(request,token);}
            public Task<Stream> OpenReadAsync(string key,CancellationToken token=default)=>inner.OpenReadAsync(key,token);
            public Task<StoredFile?> GetMetadataAsync(string key,CancellationToken token=default)=>inner.GetMetadataAsync(key,token);
            public Task<IReadOnlyList<StoredFile>> GetVersionsAsync(string key,CancellationToken token=default)=>inner.GetVersionsAsync(key,token);
            public Task DeleteAsync(string key,CancellationToken token=default)=>inner.DeleteAsync(key,token);
            public Task<StorageHealth> CheckHealthAsync(CancellationToken token=default)=>inner.CheckHealthAsync(token);
        }
        private sealed class Environment(string root):IWebHostEnvironment
        {
            public string EnvironmentName {get;set;}="Test";public string ApplicationName {get;set;}="Aegis.Studio.Web";
            public string WebRootPath {get;set;}=root;public IFileProvider WebRootFileProvider {get;set;}=new NullFileProvider();
            public string ContentRootPath {get;set;}=root;public IFileProvider ContentRootFileProvider {get;set;}=new NullFileProvider();
        }
    }
}

