using System.Text.Json;
using Aegis.Studio.Application.EmailRouting;
using Aegis.Studio.Web.Email;
using Npgsql;
using Common.Messaging;
using Common.Messaging.Channels.MicrosoftGraph;
using Microsoft.Extensions.Configuration;
using Xunit;
using Fixture=Aegis.Studio.Web.Tests.UnifiedMessagingPersistenceTests.Fixture;

namespace Aegis.Studio.Web.Tests;

[CollectionDefinition("Studio email database",DisableParallelization=true)]
public sealed class StudioEmailDatabaseCollection {}

[Collection("Studio email database")]
public sealed class EmailAssociationTests
{
    [Theory]
    [InlineData(" Client@Example.Test ","client@example.test")]
    [InlineData("Client <CLIENT@Example.Test>","client@example.test")]
    [InlineData("invalid","")]
    public void NormalizationDoesNotInventProviderAliases(string input,string expected)
        =>Assert.Equal(expected,StudioEmailRouting.NormalizeAddress(input));
    [Fact]
    public void ReferencesAreStableAndCannotTruncateToACollidingPrefix()
    {
        var id=Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        Assert.Equal("AEGIS-AAAAAAAABBBBCCCCDDDDEEEEEEEEEEEE",StudioEmailRouting.PublicReference(id));
        Assert.Single(StudioEmailRouting.ExplicitReferences("[AEGIS-AAAAAAAABBBBCCCCDDDDEEEEEEEEEEEE] Shoot"));
        Assert.Empty(StudioEmailRouting.ExplicitReferences("AEGIS-AAAAAAAA1"));
        Assert.Equal(new[]{"<one@studio.test>","<two@studio.test>"},StudioEmailRouting.MessageIds("<one@studio.test> <two@studio.test>"));
    }
    [Fact]
    public void ProviderReceiptUsesActualIdAndChecksIdentityAndReplyDestination()
    {
        using var json=JsonDocument.Parse("""
            {"id":"provider-item","internetMessageId":"<actual@exchange.test>",
             "internetMessageHeaders":[{"name":"X-Studio-Message-Id","value":"delivery"}],
             "from":{"emailAddress":{"address":"workspace@studio.test"}},
             "toRecipients":[{"emailAddress":{"address":"client@example.test"}}],
             "replyTo":[{"emailAddress":{"address":"monitor@studio.test"}}]}
            """);
        var item=json.RootElement;
        Assert.Equal("<actual@exchange.test>",StudioGraphSentMessageVerifier.TryMatch(item,"workspace@studio.test","client@example.test","monitor@studio.test","delivery")!.InternetMessageId);
        Assert.Null(StudioGraphSentMessageVerifier.TryMatch(item,"other@studio.test","client@example.test","monitor@studio.test","delivery"));
        Assert.Null(StudioGraphSentMessageVerifier.TryMatch(item,"workspace@studio.test","client@example.test","unmonitored@studio.test","delivery"));
        Assert.Null(StudioGraphSentMessageVerifier.TryMatch(item,"workspace@studio.test","client@example.test","monitor@studio.test","other"));
    }
    [Fact]
    public async Task CommonMessagingMimeAndProviderReadBackUseActualInternetId()
    {
        var handler=new FakeGraph();using var http=new HttpClient(handler);
        var secret=new FakeSecrets();
        var channel=new MicrosoftGraphEmailMessageChannel(http,new(){TenantId="tenant",ClientId="client",SenderUpn="hub@studio.test",ClientSecretName="test",SkipSenderRecipient=false},secret);
        Guid id=Guid.NewGuid();string correlation=id.ToString("D");
        await channel.SendAsync(new("client","Client","client@example.test"),new(){Title="Inquiry",Body="Body",Metadata=new Dictionary<string,string>{
            ["Email.MessageId"]="<proposed@studio.test>",["Email.From"]="workspace@studio.test",["Email.ReplyTo"]="monitor@studio.test",["Studio.MessageId"]=correlation}},id);
        Assert.Contains("Message-ID: <proposed@studio.test>",handler.Mime);
        Assert.Contains("Reply-To: monitor@studio.test",handler.Mime);
        Assert.Contains("From: workspace@studio.test",handler.Mime);
        var configuration=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Messaging:MicrosoftGraph:Email:SenderUpn","hub@studio.test"},{"Messaging:MicrosoftGraph:TenantId","tenant"},{"Messaging:MicrosoftGraph:ClientId","client"},{"Messaging:MicrosoftGraph:ClientSecretName","test"}}).Build();
        var delivery=new OutboundEmail(id,Guid.NewGuid(),"workspace@studio.test","client@example.test","Inquiry","Body",null,"Sending",DateTimeOffset.UtcNow,null);
        var verified=await new StudioGraphSentMessageVerifier(http,configuration,secret).VerifyAsync(delivery,correlation,"monitor@studio.test",DateTimeOffset.UtcNow.AddMinutes(-1),default);
        Assert.Equal("<actual@exchange.test>",verified.InternetMessageId);Assert.Equal("provider-item",verified.ProviderId);
        Assert.Equal(1,handler.Sends);Assert.Equal(1,handler.Reads);
    }
    private sealed class FakeSecrets:IChannelSecretResolver
    {public Task<string?> GetSecretAsync(string name,CancellationToken token=default)=>Task.FromResult<string?>("test-only");}
    private sealed class FakeGraph:HttpMessageHandler
    {
        public string Mime {get;private set;}="";public int Sends {get;private set;}public int Reads {get;private set;}
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(request.RequestUri!.Host=="login.microsoftonline.com")return new(System.Net.HttpStatusCode.OK){Content=new StringContent("{\"access_token\":\"fake-token\"}")};
            Assert.Equal("graph.microsoft.com",request.RequestUri.Host);
            Assert.StartsWith("/v1.0/users/hub@studio.test/",Uri.UnescapeDataString(request.RequestUri.AbsolutePath));
            if(request.Method==HttpMethod.Post){Sends++;Mime=System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(await request.Content!.ReadAsStringAsync(token)));return new(System.Net.HttpStatusCode.Accepted);}
            Reads++;string correlation=System.Text.RegularExpressions.Regex.Match(Mime,@"X-Studio-Message-Id: ([^\r\n]+)").Groups[1].Value;
            var item=new{id="provider-item",internetMessageId="<actual@exchange.test>",internetMessageHeaders=new[]{new{name="X-Studio-Message-Id",value=correlation}},
                from=new{emailAddress=new{address="workspace@studio.test"}},toRecipients=new[]{new{emailAddress=new{address="client@example.test"}}},replyTo=new[]{new{emailAddress=new{address="monitor@studio.test"}}}};
            return new(System.Net.HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{value=new[]{item}}))};
        }
    }
    [StudioPostgresFact]
    public async Task UniqueZeroAndMultipleClientMatchesAndStaffCorrectionsCarryIntoJob()
    {
        using var f=await Fixture.Create();var workspace=Workspace(f);Guid client=Guid.NewGuid();
        Sql(f,"insert into studio_clients(id,display_name,email) values($1,'Client',' CLIENT@Example.Test ')",client);
        var first=await f.Intake.ReceiveAsync(Email("unique","client@example.test"),default);
        Assert.Equal(client,f.Associations.Get(first.ConversationId!.Value,f.Administrator).ClientId);
        Assert.Single(f.Associations.Get(first.ConversationId.Value,f.Administrator).History);
        var zero=await f.Intake.ReceiveAsync(Email("zero","other@example.test"),default);
        Assert.Null(f.Associations.Get(zero.ConversationId!.Value,f.Administrator).ClientId);
        Sql(f,"insert into studio_clients(id,display_name,email) values($1,'Other client','client@example.test')",Guid.NewGuid());
        var multiple=await f.Intake.ReceiveAsync(Email("multiple","client@example.test"),default);
        var association=f.Associations.Get(multiple.ConversationId!.Value,f.Administrator);Assert.Null(association.ClientId);Assert.Equal(2,association.Candidates.Length);
        f.Associations.SetClient(multiple.ConversationId.Value,client,f.Administrator);
        f.Associations.SetClient(multiple.ConversationId.Value,null,f.Administrator);
        await f.Intake.ReceiveAsync(Email("multiple","client@example.test"),default);
        Assert.Null(f.Associations.Get(multiple.ConversationId.Value,f.Administrator).ClientId);
        f.Associations.SetClient(multiple.ConversationId.Value,client,f.Administrator);
        var job=f.Studio.CreateJob(multiple.ConversationId.Value,new("New work",null));
        Assert.Equal(client,job.ClientId);Assert.Equal(multiple.ConversationId,job.ConversationId);Assert.Equal(workspace,job.WorkspaceId);
        Assert.Throws<UnauthorizedAccessException>(()=>f.Associations.SetClient(zero.ConversationId.Value,client,f.Other));
    }
    [StudioPostgresFact]
    public async Task SeparateInquiriesRepliesFreshReferencesAndPrePromotionCorrespondenceStayDistinct()
    {
        using var f=await Fixture.Create();Workspace(f);
        var one=await f.Intake.ReceiveAsync(Email("one"),default);var two=await f.Intake.ReceiveAsync(Email("two"),default);
        Assert.NotEqual(one.ConversationId,two.ConversationId);
        var before=f.Outbox.Queue(f.Studio.GetConversation(one.ConversationId!.Value)!,"Before job");Verify(f,before.Id,"<before@studio.test>");
        Sql(f,"update studio_outbound_email set rfc_message_id='<local-only@studio.test>',rfc_message_id_verified=false where id=$1",before.Id);
        Assert.False((await f.Intake.ReceiveAsync(Email("local-only-reply",reply:"<local-only@studio.test>"),default)).Routed);
        Sql(f,"update studio_outbound_email set rfc_message_id='<before@studio.test>',rfc_message_id_verified=true where id=$1",before.Id);
        var job1=f.Studio.CreateJob(one.ConversationId.Value,new("Job one",null));var job2=f.Studio.CreateJob(two.ConversationId!.Value,new("Job two",null));
        var sent2=f.Outbox.Queue(f.Studio.GetConversation(two.ConversationId.Value)!,"Job two message");Verify(f,sent2.Id,"<two@studio.test>");
        Assert.Equal(one.ConversationId,(await f.Intake.ReceiveAsync(Email("reply1",reply:"<before@studio.test>"),default)).ConversationId);
        Assert.Equal(two.ConversationId,(await f.Intake.ReceiveAsync(Email("reply2",reply:"<two@studio.test>"),default)).ConversationId);
        Assert.Equal(two.ConversationId,(await f.Intake.ReceiveAsync(Email("fresh",subject:StudioEmailRouting.PublicReference(two.ConversationId.Value)),default)).ConversationId);
        var unfamiliar=await f.Intake.ReceiveAsync(Email("stranger","unknown@example.test",subject:StudioEmailRouting.PublicReference(two.ConversationId.Value)),default);
        Assert.False(unfamiliar.Routed);Assert.NotNull(unfamiliar.ReviewId);
        var noReference=await f.Intake.ReceiveAsync(Email("new-with-same-subject"),default);Assert.NotEqual(one.ConversationId,noReference.ConversationId);Assert.NotEqual(two.ConversationId,noReference.ConversationId);
        var conflict=Email("conflict",reply:"<before@studio.test>") with{InternetHeaders=new Dictionary<string,string>{{"In-Reply-To","<before@studio.test>"},{"References","<two@studio.test>"}}};
        Assert.False((await f.Intake.ReceiveAsync(conflict,default)).Routed);
        Assert.Equal(job1.Id,f.Studio.GetConversation(one.ConversationId.Value)!.JobId);Assert.NotEqual(job1.Id,job2.Id);
    }
    [StudioPostgresFact]
    public async Task AttachmentsDuplicatesAndSplitRetainIdentityAndCorrections()
    {
        using var f=await Fixture.Create();Workspace(f);
        var email=Email("with-file") with{Attachments=[new("brief.txt","text/plain",[65,66,67])],ProviderMessageId="provider-original"};
        var first=await f.Intake.ReceiveAsync(email,default);var repeat=await f.Intake.ReceiveAsync(email,default);Assert.True(repeat.WasDuplicate);Assert.Equal(first.ConversationId,repeat.ConversationId);
        Assert.Equal(1,f.Count("studio_attachments"));Assert.Equal(1,f.Count("studio_messages"));
        var message=Assert.Single(f.Studio.GetConversation(first.ConversationId!.Value)!.Messages);
        var moved=f.Associations.MoveMessage(first.ConversationId.Value,message.Id,new(null,null,true),f.Administrator);
        Assert.Equal(message.Id,Assert.Single(f.Studio.GetConversation(moved)!.Messages).Id);
        Assert.Equal(moved,(await f.Intake.ReceiveAsync(email,default)).ConversationId);
        Assert.Equal(1,f.Count("studio_attachments"));Assert.Equal(1,f.Count("studio_messages"));
        Assert.False((await f.Intake.ReceiveAsync(email with{Body="Different content"},default)).Routed);
        var otherProviderId=email with{MessageId="<changed-id@example.test>"};
        Assert.Equal(moved,(await f.Intake.ReceiveAsync(otherProviderId,default)).ConversationId);Assert.Equal(1,f.Count("studio_messages"));
    }
    [StudioPostgresFact]
    public async Task StaffReviewAndAlternateContactsAreExplicitAndLegacyCollisionsAreHeld()
    {
        using var f=await Fixture.Create();Guid workspace=Workspace(f);
        var first=await f.Intake.ReceiveAsync(Email("original"),default);Guid inquiry=first.ConversationId!.Value;
        var unknown=Email("review","alternate@example.test",StudioEmailRouting.PublicReference(inquiry));
        Assert.False((await f.Intake.ReceiveAsync(unknown,default)).Routed);
        Assert.Equal(inquiry,(await f.Intake.ReceiveAsync(unknown,default,confirmed:new(inquiry,null),actor:"reviewer")).ConversationId);
        Assert.Contains(f.Associations.Get(inquiry,f.Administrator).History,h=>JsonSerializer.Serialize(h).Contains("reviewer"));
        Assert.False((await f.Intake.ReceiveAsync(unknown with{MessageId="<another@example.test>"},default)).Routed);
        f.Associations.AddContact(inquiry,"ALTERNATE@Example.Test",f.Administrator);
        Assert.True((await f.Intake.ReceiveAsync(unknown with{MessageId="<recognized@example.test>"},default)).Routed);
        var separate=await f.Intake.ReceiveAsync(Email("staff-new",subject:StudioEmailRouting.PublicReference(inquiry)),default,
            fallback:new(true,workspace,"Studio",null,null),actor:"reviewer",confirmNewInquiry:true);
        Assert.NotEqual(inquiry,separate.ConversationId);Assert.Equal(0,f.Count("studio_outbound_email"));
        foreach(var id in new[]{Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111"),Guid.Parse("aaaaaaaa-2222-2222-2222-222222222222")})
            Sql(f,"insert into studio_conversations(id,workspace_id,subject,contact_name,contact_address,channel,created_at_utc) values($1,$2,'Old','Client','client@example.test','Email',now())",id,workspace);
        Assert.False((await f.Intake.ReceiveAsync(Email("collision",subject:"[AEGIS-AAAAAAAA] New message"),default)).Routed);
    }
    [StudioPostgresFact]
    public async Task HistoricalWorkspaceRepliesRequireReviewAndMigrationIsAdditive()
    {
        using var f=await Fixture.Create();Guid workspace=Workspace(f),thread=Guid.NewGuid(),delivery=Guid.NewGuid();
        Sql(f,"insert into studio_message_threads(id,scope_type,scope_id,title,created_by,created_at_utc) values($1,'Workspace',$2,'Historical shared mail','staff',now())",thread,workspace);
        Sql(f,"insert into studio_outbound_email(id,studio_thread_id,from_address,to_address,subject,body,status,created_at_utc,kind,sending_identity_type,rfc_message_id,rfc_message_id_verified) values($1,$2,'workspace@studio.test','client@example.test','Historical','Body','ProviderAccepted',now(),'Unified','Workspace','<old-shared@studio.test>',true)",delivery,thread);
        var email=Email("historical-reply",reply:"<old-shared@studio.test>");
        Assert.Contains("Historical shared workspace",f.Routing.Resolve(email).ReviewReason);
        Assert.False((await f.Intake.ReceiveAsync(email,default)).Routed);
        Assert.True(f.Associations.CanReviewThread(thread,f.Administrator));Assert.False(f.Associations.CanReviewThread(thread,f.Other));
        f.Associations.EnsureSchema();f.Associations.EnsureSchema();
        Assert.Equal(1,f.Count("studio_outbound_email"));Assert.Equal(1,f.Count("studio_unrouted_email"));
        var confirmed=await f.Intake.ReceiveAsync(email,default,fallback:new(true,workspace,"Studio",null,null),actor:"reviewer",confirmNewInquiry:true);
        Assert.True(confirmed.Routed);Assert.NotNull(confirmed.ConversationId);Assert.Equal(0,f.Count("studio_inapp_messages"));
    }
    [StudioPostgresFact]
    public async Task InterruptedAttachmentImportResumesWithoutDuplicatingMessageOrFiles()
    {
        using var f=await Fixture.Create(interruptAttachment:true);Workspace(f);
        var email=Email("interrupted") with {Attachments=[new("one.txt","text/plain",[65]),new("two.txt","text/plain",[66])]};
        await Assert.ThrowsAsync<Common.Storage.StorageException>(()=>f.Intake.ReceiveAsync(email,default));
        Assert.Equal(1,f.Count("studio_messages"));Assert.Equal(1,f.Count("studio_attachments"));Assert.Equal(1,f.Count("studio_email_intake_receipts"));
        Assert.False((await f.Intake.ReceiveAsync(email with{Attachments=[new("replacement.txt","text/plain",[67])]},default)).Routed);
        var resumed=await f.Intake.ReceiveAsync(email,default);Assert.True(resumed.Routed);
        Assert.Equal(1,f.Count("studio_messages"));Assert.Equal(2,f.Count("studio_attachments"));
        using var c=f.Source.OpenConnection();using var q=new NpgsqlCommand("select attachments_complete from studio_email_intake_receipts",c);Assert.True((bool)q.ExecuteScalar()!);
    }
    private static InboundEmail Email(string id,string from="client@example.test",string subject="New shoot",string? reply=null)
        =>new($"<{id}@example.test>","provider-thread-same", "Client",from,["workspace@studio.test"],[],reply is null?new Dictionary<string,string>():new Dictionary<string,string>{{"In-Reply-To",reply}},subject,"Original inquiry",DateTimeOffset.UtcNow);
    private static Guid Workspace(Fixture f)
    {Guid id=Guid.NewGuid();Sql(f,"insert into studio_workspaces(id,name,email_address,created_at_utc,updated_at_utc) values($1,'Studio','workspace@studio.test',now(),now())",id);return id;}
    private static void Verify(Fixture f,Guid id,string actual)
    {Guid lease=Guid.NewGuid();Sql(f,"update studio_outbound_email set lease_id=$2 where id=$1",id,lease);f.Outbox.RecordVerifiedInternetId(id,lease,new("fake-provider",actual));f.Outbox.MarkProviderAccepted(id,"fake-provider",lease);}
    private static void Sql(Fixture f,string sql,params object[] values)
    {using var c=f.Source.OpenConnection();using var q=new NpgsqlCommand(sql,c);foreach(var value in values)q.Parameters.AddWithValue(value);q.ExecuteNonQuery();}
}
