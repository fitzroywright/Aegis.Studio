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
        var lease=Guid.NewGuid();var claimed=f.Outbox.ClaimNext(["Email"],lease)!;f.Outbox.MarkProviderAccepted(claimed.Id,claimed.RfcMessageId,lease);
        var reply=new InboundEmail("<reply@outside.test>",null,"Client","client@outside.test",["router@studio.test"],[],
            new Dictionary<string,string>{{"In-Reply-To",outbound.RfcMessageId!}},"Re: Client booking","Thank you",DateTimeOffset.UtcNow,
            [new("confirmation.txt","text/plain",Encoding.UTF8.GetBytes("Confirmed"))]);
        Assert.True(await f.Inbound.TryImportAsync(reply,default));Assert.True(await f.Inbound.TryImportAsync(reply,default));
        Assert.Equal(2,f.Messages.GetMessages(sent.ThreadId,f.Paul).Count);Assert.Equal(1,f.Count("studio_inbound_receipts"));
        Assert.Equal(2,f.Attachments.GetForThread(sent.ThreadId).Count);Assert.Single(f.Book.GetAll().Where(e=>e.EmailAddress=="client@outside.test"));
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
    private static FormFileCollection Files(string name,string content)
    {
        var stream=new MemoryStream(Encoding.UTF8.GetBytes(content));return [new FormFile(stream,0,stream.Length,"files",name){Headers=new HeaderDictionary(),ContentType="text/plain"}];
    }
    private sealed class Fixture:IDisposable
    {
        private readonly string schema="studio_messaging_test_"+Guid.NewGuid().ToString("N");
        private readonly string root=Path.Combine(Path.GetTempPath(),"studio-messaging-tests",Guid.NewGuid().ToString("N"));
        private readonly string originalConnection=System.Environment.GetEnvironmentVariable("STUDIO_TEST_POSTGRES")!;
        private ServiceProvider services=null!;
        public NpgsqlDataSource Source {get;private set;}=null!;
        public StudioAddressBook Book {get;private set;}=null!;public StudioUnifiedMessaging Router {get;private set;}=null!;
        public StudioInAppMessageStore Messages {get;private set;}=null!;public StudioMessageAttachmentStore Attachments {get;private set;}=null!;
        public StudioUnifiedInbound Inbound {get;private set;}=null!;public OutboundEmailStore Outbox {get;private set;}=null!;
        public UnifiedMessagingTests.Directory Directory {get;}=new();
        public ClaimsPrincipal Paul {get;}=User("paul");public ClaimsPrincipal Tamara {get;}=User("tamara");public ClaimsPrincipal Other {get;}=User("other");
        public ClaimsPrincipal Administrator {get;}=new(new ClaimsIdentity([new Claim("login_name","administrator"),new Claim(ClaimTypes.Role,"Administrator")],"test"));
        public static async Task<Fixture> Create()
        {
            var f=new Fixture();
            using(var c=new NpgsqlConnection(f.originalConnection)){await c.OpenAsync();using var q=new NpgsqlCommand($"create schema {f.schema}",c);await q.ExecuteNonQueryAsync();}
            f.Source=NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(f.originalConnection){SearchPath=f.schema}.ConnectionString);
            var db=new StudioDatabase(f.Source);await db.InitializeAsync();await new StudioFeatureDatabase(f.Source).InitializeAsync();await new StudioMessagingDatabase(f.Source).InitializeAsync();
            using(var c=f.Source.OpenConnection())using(var q=new NpgsqlCommand("""
                create table studio_clients(id uuid primary key,display_name text not null,email text);
                create table studio_photographers(user_name text,display_name text,is_active boolean);
                create table studio_global_operational_roles(id uuid,name text,is_active boolean);
                create table studio_global_operational_role_members(role_id uuid,user_name text,display_name text,is_active boolean);
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
            f.Inbound=new(db,f.Book,f.Attachments,new UnroutedEmailStore(db),config,f.services.GetRequiredService<IHubContext<StudioMessageHub>>(),f.Messages);return f;
        }
        public void VerifyPaul(){var paul=Book.GetAll().Single(e=>e.UserName=="paul");Book.Save(paul with{EmailAddress="paul@studio.test",SenderMailbox="router@studio.test",SenderVerified=true,VerificationEvidence="Fake-provider unit fixture only; no live-tenant claim."});}
        public long Count(string table){using var c=Source.OpenConnection();using var q=new NpgsqlCommand("select count(*) from "+table,c);return (long)q.ExecuteScalar()!;}
        public void Dispose(){services?.Dispose();Source?.Dispose();using var c=new NpgsqlConnection(originalConnection);c.Open();using var q=new NpgsqlCommand($"drop schema if exists {schema} cascade",c);q.ExecuteNonQuery();if(System.IO.Directory.Exists(root))System.IO.Directory.Delete(root,true);}
        private static ClaimsPrincipal User(string name)=>new(new ClaimsIdentity([new Claim("login_name",name),new Claim(ClaimTypes.Name,name),new Claim(ClaimTypes.Email,name+"@studio.test")],"test"));
        private sealed class Environment(string root):IWebHostEnvironment
        {
            public string EnvironmentName {get;set;}="Test";public string ApplicationName {get;set;}="Aegis.Studio.Web";
            public string WebRootPath {get;set;}=root;public IFileProvider WebRootFileProvider {get;set;}=new NullFileProvider();
            public string ContentRootPath {get;set;}=root;public IFileProvider ContentRootFileProvider {get;set;}=new NullFileProvider();
        }
    }
}
