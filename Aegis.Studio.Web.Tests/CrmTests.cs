using System.Security.Claims;
using System.Text.Json;
using Aegis.Studio.Web.Crm;
using Aegis.Studio.Web.FrontDesk;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Workflow;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;
namespace Aegis.Studio.Web.Tests;

public sealed class CrmTests
{
    private static JsonElement Json(string value)=>JsonSerializer.Deserialize<JsonElement>(value);
    [Fact] public void NormalizationDoesNotMergeNamesOrInventEmailAliases(){Assert.Equal("name+alias@example.test",CrmRules.NormalizeEmail(" NAME+Alias@Example.Test "));Assert.Equal("18765550123",CrmRules.NormalizePhone("+1 (876) 555-0123"));}
    [Fact] public void SortIsWhitelistedAndFinancialSortRequiresPermission(){Assert.Contains("desc",CrmRules.OrderBy("name,-jobs",false));Assert.Throws<ArgumentException>(()=>CrmRules.OrderBy("name;drop table",true));Assert.Throws<ArgumentException>(()=>CrmRules.OrderBy("value",false));}
    [Fact] public void CsvHandlesQuotedCommasNewlinesAndEscapedQuotes(){var rows=CrmCsv.Parse("Display Name,Email\r\n\"Campbell, \"\"Nia\"\"\",nia@example.test\r\n\"Two\nLines\",x@example.test");Assert.Equal("Campbell, \"Nia\"",rows[1][0]);Assert.Equal("Two\nLines",rows[2][0]);Assert.StartsWith("\"'",CrmCsv.Cell("=HYPERLINK()"));Assert.Throws<ArgumentException>(()=>CrmCsv.Parse("\"unfinished"));}
    [Fact] public void InvalidTypesAndTaskStatesAreRejected(){Assert.Throws<ArgumentException>(()=>CrmRules.Validate(new CrmClientRequest("Client",ClientType:"Unsupported")));Assert.Throws<ArgumentException>(()=>CrmRules.Validate(new CrmTaskRequest(Guid.NewGuid(),"staff","Unsupported",DateTimeOffset.UtcNow)));}

    [StudioPostgresFact] public async Task AdditiveMigrationPreservesIdentityAndIsRepeatable()
    {
        await using var f=await Fixture.Create();var id=f.Admin.Save(null,f.Request("Existing"));CrmStore.Migrate(f.Db);Assert.Equal("Existing",f.Admin.Get(id).GetProperty("displayName").GetString());Assert.Equal(4,f.Count("studio_crm_migrations"));
    }
    [StudioPostgresFact] public async Task CreateUpdateSearchFilterBulkViewsAndArchive()
    {
        await using var f=await Fixture.Create();Guid id=f.Admin.Save(null,f.Request("Campbell Family") with{ClientType="Family / Household",Tags=["Returning"],Metadata=Json("{\"alternateNames\":\"Nia household\"}")});
        Assert.Equal(1,f.Admin.Directory(new(Search:"Nia household")).GetProperty("total").GetInt32());Assert.Equal(0,f.Admin.Directory(new(Type:"School")).GetProperty("total").GetInt32());
        f.Admin.Save(id,f.Request("Campbell Household") with{ClientType="Family / Household"});f.Admin.Bulk(new([id],["Repeat"],"Inactive"));Assert.Equal(1,f.Admin.Directory(new(Status:"Inactive")).GetProperty("total").GetInt32());
        f.Admin.SaveView(new("Schools",Json("{\"type\":\"School\"}")));Assert.Single(f.Admin.Views().EnumerateArray());f.Admin.Archive(id);Assert.Equal(0,f.Admin.Directory(new()).GetProperty("total").GetInt32());Assert.Equal(1,f.Admin.Directory(new(Status:"Archived")).GetProperty("total").GetInt32());
    }
    [StudioPostgresFact] public async Task WorkspaceIsolationAndSensitivePermissions()
    {
        await using var f=await Fixture.Create();var a=f.Admin.Save(null,f.Request("Accessible"));var b=f.Admin.Save(null,f.Request("Restricted") with{PrimaryWorkspaceId=f.OtherWorkspace});
        var scoped=f.For("viewer",[CrmPermissions.View]);Assert.Equal(1,scoped.Directory(new()).GetProperty("total").GetInt32());Assert.Throws<KeyNotFoundException>(()=>scoped.Get(b));Assert.Equal(a,scoped.Get(a).GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null,scoped.Directory(new()).GetProperty("items")[0].GetProperty("lifetimeValue").ValueKind);
        Assert.Throws<UnauthorizedAccessException>(()=>scoped.Financials(a));Assert.Throws<UnauthorizedAccessException>(()=>scoped.Export(new()));Assert.Throws<UnauthorizedAccessException>(()=>scoped.PreviewMerge(a,b));Assert.Throws<UnauthorizedAccessException>(()=>scoped.Save(a,f.Request("No edit")));
        Assert.Throws<UnauthorizedAccessException>(()=>new CrmStore(f.Db,Fixture.User("stranger")).Directory(new()));
    }
    [StudioPostgresFact] public async Task RelationshipsReusePeopleAcrossHouseholdsAndRejectSelfLinks()
    {
        await using var f=await Fixture.Create();var person=f.Admin.Save(null,f.Request("Nia"));var family=f.Admin.Save(null,f.Request("Household"));var school=f.Admin.Save(null,f.Request("School"));
        f.Admin.AddContact(family,new(person,"Parent",true,true));f.Admin.AddContact(school,new(person,"School Administrator",false,true));f.Admin.AddContact(family,new(person,"Parent",true,true));Assert.Single(f.Admin.Contacts(family).EnumerateArray());Assert.Single(f.Admin.Contacts(school).EnumerateArray());Assert.Equal(3,f.Count("studio_clients"));Assert.Throws<ArgumentException>(()=>f.Admin.AddContact(person,new(person,"Self")));
    }
    [StudioPostgresFact] public async Task DuplicateMatchingAndMergePreviewProtectLinkedHistory()
    {
        await using var f=await Fixture.Create();var a=f.Admin.Save(null,f.Request("Same") with{Email="a@example.test"});var b=f.Admin.Save(null,f.Request("Same") with{Email=" A@example.test "});var justName=f.Admin.Save(null,f.Request("Same"));var job=f.Job(b);Assert.Single(f.Admin.Duplicates(a).EnumerateArray());
        var preview=f.Admin.PreviewMerge(a,b);f.Admin.Note(b,new("Changed after preview"));Assert.Throws<InvalidOperationException>(()=>f.Admin.Merge(a,new(b,"Reviewed",preview.GetProperty("token").GetString()!)));
        preview=f.Admin.PreviewMerge(a,b);f.Admin.Merge(a,new(b,"Confirmed same client",preview.GetProperty("token").GetString()!));Assert.Equal(a,f.Scalar("select client_id from studio_jobs where id=$1",job));Assert.Equal(a,f.Admin.Get(b).GetProperty("mergedIntoId").GetGuid());Assert.Equal(3,f.Count("studio_clients"));Assert.Equal(1,f.Admin.Section(a,"jobs").GetProperty("total").GetInt32());Assert.Empty(f.Admin.Duplicates(justName).EnumerateArray());
    }
    [StudioPostgresFact] public async Task TasksValidateOwnershipCompleteReassignAndDetectOverdue()
    {
        await using var f=await Fixture.Create();var client=f.Admin.Save(null,f.Request("Client"));var job=f.Job(client);var request=new CrmTaskRequest(f.Workspace,"viewer","Call Client",DateTimeOffset.UtcNow.AddHours(-2),JobId:job);var task=f.Admin.SaveTask(client,null,request);Assert.Single(f.Admin.Tasks(overdue:true).EnumerateArray());
        f.Admin.SaveTask(client,task,request with{AssignedTo="assistant",Status="In Progress"});Assert.Equal("assistant",f.Admin.Tasks(client)[0].GetProperty("assignedTo").GetString());f.Admin.SaveTask(client,task,request with{Status="Completed"});Assert.Empty(f.Admin.Tasks(overdue:true).EnumerateArray());Assert.Equal(JsonValueKind.String,f.Admin.Tasks(client)[0].GetProperty("completedAtUtc").ValueKind);Assert.Throws<ArgumentException>(()=>f.Admin.SaveTask(client,null,request with{AssignedTo="unknown"}));
        var other=f.Admin.Save(null,f.Request("Other"));Assert.Throws<ArgumentException>(()=>f.Admin.SaveTask(other,null,request));
    }
    [StudioPostgresFact] public async Task ProfileUsesCorrectJobsSessionsCommunicationsLedgerAndTimeline()
    {
        await using var f=await Fixture.Create();var client=f.Admin.Save(null,f.Request("Portrait client"));var other=f.Admin.Save(null,f.Request("Other client"));var job=f.Job(client);f.Job(other);
        f.Sql("insert into studio_financial_events(id,job_id,kind,amount,created_at_utc) values(gen_random_uuid(),$1,'Charge',1000,now()),(gen_random_uuid(),$1,'Payment',600,now()),(gen_random_uuid(),$1,'Refund',-100,now())",job);
        var financials=f.Admin.Financials(client);Assert.Equal(500,financials.GetProperty("lifetimeValue").GetDecimal());Assert.Equal(500,financials.GetProperty("outstandingBalance").GetDecimal());Assert.Equal(0,financials.GetProperty("totalInvoiced").GetDecimal());
        Assert.Equal(1,f.Admin.Section(client,"jobs").GetProperty("total").GetInt32());Assert.Equal(0,f.Admin.Section(client,"sessions").GetProperty("total").GetInt32());
        foreach(var section in new[]{"communications","documents","galleries","activity","enquiries"})Assert.Equal(JsonValueKind.Array,f.Admin.Section(client,section).GetProperty("items").ValueKind);
        Assert.Contains(f.Admin.Section(client,"activity").GetProperty("items").EnumerateArray(),e=>e.GetProperty("eventType").GetString()=="JobCreated");
        Assert.Equal(500,f.Admin.Directory(new(Search:"Portrait client")).GetProperty("items")[0].GetProperty("lifetimeValue").GetDecimal());
    }
    [StudioPostgresFact] public async Task CsvImportIsAtomicScopedAndDetectsExistingContacts()
    {
        await using var f=await Fixture.Create();var request=new CrmImportRequest("Display Name,Email,Telephone,Client Type\nNia,nia@example.test,8765550111,Individual\nFamily,family@example.test,8765550112,Family / Household",f.Workspace);
        f.Admin.ImportPreview(request);f.Admin.Import(request);Assert.Equal(2,f.Count("studio_clients"));Assert.Throws<ArgumentException>(()=>f.Admin.Import(request));Assert.Equal(2,f.Count("studio_clients"));Assert.Contains("Nia",f.Admin.Export(new()));
        Assert.Throws<ArgumentException>(()=>f.Admin.Import(new("Display Name,Email\nValid,valid@example.test\nInvalid,not email",f.Workspace)));Assert.Equal(2,f.Count("studio_clients"));
    }
    [StudioPostgresFact] public async Task AutomationIsWorkspaceOptInAtomicAndIdempotent()
    {
        await using var f=await Fixture.Create();var client=f.Admin.Save(null,f.Request("Client") with{AccountOwner="viewer"});var action=Json("{\"type\":\"CreateFollowUpTask\",\"taskType\":\"Call Client\",\"dueMinutes\":-1}");
        Assert.Throws<InvalidOperationException>(()=>CrmAutomation.Execute(f.Db,client,null,action,"run:0"));Assert.Equal(0,f.Count("studio_crm_automation_receipts"));f.Sql("update studio_workspaces set crm_automation_enabled=true where id=$1",f.Workspace);
        CrmAutomation.Execute(f.Db,client,null,action,"run:0");CrmAutomation.Execute(f.Db,client,null,action,"run:0");Assert.Equal(1,f.Count("studio_crm_tasks"));Assert.Equal(1,f.Count("studio_crm_automation_receipts"));Assert.Single(f.Admin.Tasks(overdue:true).EnumerateArray());
        CrmAutomation.Execute(f.Db,client,null,Json("{\"type\":\"SendCrmNotification\"}"),"run:1");Assert.Equal(1,f.Count("studio_notifications"));Assert.Equal(0,f.Count("studio_outbound_email"));
    }
    [StudioPostgresFact] public async Task ConsentBlocksQueuedAndRetriedDeliveryWithoutInventingSuccess()
    {
        await using var f=await Fixture.Create();var client=f.Admin.Save(null,f.Request("Client") with{Metadata=Json("{\"communicationPreferences\":{\"emailAllowed\":false}}")});var job=f.Job(client);var conversation=(Guid)f.Scalar("select conversation_id from studio_jobs where id=$1",job)!;
        f.Sql("insert into studio_outbound_email(id,conversation_id,from_address,to_address,subject,body,status,kind,created_at_utc) values(gen_random_uuid(),$1,'studio@example.test','client@example.test','Test','Test','Pending','Reply',now())",conversation);
        Assert.Equal("Attention",f.Scalar("select status from studio_outbound_email"));f.Sql("update studio_outbound_email set status='Retry'");Assert.Equal("Attention",f.Scalar("select status from studio_outbound_email"));Assert.Contains("consent",(string)f.Scalar("select last_error from studio_outbound_email")!);
    }
    [StudioPostgresFact] public async Task ExistingWorkflowRunnerExecutesCrmTriggersOnceWithoutLoops()
    {
        await using var f=await Fixture.Create();
        var workspaces=new WorkspaceStore(f.Db);var studio=new StudioStore(f.Db,workspaces);
        var engine=new WorkflowAutomationEngine(f.Db,studio,null!,new(f.Db),new(f.Db),new(f.Db,workspaces),new ConfigurationBuilder().Build());engine.EnsureSchema();
        f.Sql("update studio_workspaces set crm_automation_enabled=true where id=$1",f.Workspace);
        engine.SaveDefinition(null,new("New client call",null,"ClientCreated",JsonSerializer.Serialize(new{workspaceId=f.Workspace}),"[{\"type\":\"CreateFollowUpTask\",\"assignedTo\":\"viewer\",\"dueMinutes\":-1}]"));
        var client=f.Admin.Save(null,f.Request("New customer"));engine.ResumeDueRuns();engine.ResumeDueRuns();
        Assert.Equal(1,f.Count("studio_crm_tasks"));Assert.Equal(1,f.Count("studio_workflow_runs"));Assert.Equal("Completed",engine.GetRuns(10).Single().Status);
        Assert.Equal(client,f.Admin.Tasks()[0].GetProperty("clientId").GetGuid());Assert.Equal(0,f.Count("studio_outbound_email"));
    }
    [StudioPostgresFact] public async Task DemoCrmReusesJobsAndSeedsAllAccountTypesAndSharedContacts()
    {
        await using var f=await Fixture.Create();var existing=f.Admin.Save(null,f.Request("Existing Family"));f.Job(existing);f.Job(existing);
        CrmDemo.Seed(f.Db,"demo-test");int clients=f.Count("studio_clients"),contacts=f.Count("studio_crm_contacts");CrmDemo.Seed(f.Db,"demo-test");
        Assert.Equal(clients,f.Count("studio_clients"));Assert.Equal(contacts,f.Count("studio_crm_contacts"));Assert.Equal(2,f.Count("studio_jobs"));Assert.Equal(3,contacts);
        Assert.Equal(5,Convert.ToInt32(f.Scalar("select count(distinct client_type) from studio_clients")));Assert.True(f.Count("studio_crm_tasks")>0);
        Assert.Equal(0,Convert.ToInt32(f.Scalar("select count(*) from studio_crm_contacts r left join studio_clients p on p.id=r.person_id left join studio_clients c on c.id=r.client_id where p.id is null or c.id is null")));
        Assert.Equal(0,Convert.ToInt32(f.Scalar("select count(*) from studio_crm_events where processed_at_utc is null")));
    }
    [StudioPostgresFact] public async Task ActivityPaginationDoesNotLeakFinancialAuditToClientReaders()
    {
        await using var f=await Fixture.Create();var client=f.Admin.Save(null,f.Request("Client"));var job=f.Job(client);
        f.Sql("insert into studio_operational_audit(id,job_id,action,user_name,detail,created_at_utc) values(gen_random_uuid(),$1,'PaymentRecorded','finance','Private amount 999999',now())",job);
        var reader=f.For("reader",[CrmPermissions.View,StudioPermissions.ViewAllJobs]);
        f.Sql("insert into studio_workspace_members(workspace_id,user_name,display_name,is_active,access_workspace,can_view,created_at_utc,updated_at_utc) values($1,'reader','Reader',true,true,true,now(),now())",f.Workspace);
        Assert.DoesNotContain("999999",reader.Section(client,"activity").GetRawText());Assert.Contains("999999",f.Admin.Section(client,"activity").GetRawText());
        for(int i=0;i<55;i++)f.Admin.Note(client,new("Follow-up note "+i));
        Assert.Equal(50,f.Admin.Section(client,"activity").GetProperty("items").GetArrayLength());Assert.True(f.Admin.Section(client,"activity",2).GetProperty("items").GetArrayLength()>0);
    }
    [StudioPostgresFact] public async Task DirectoryMultiColumnPaginationReturnsEveryClientOnce()
    {
        await using var f=await Fixture.Create();
        foreach(var name in new[]{"Dahlia","Amara","Nia","Zuri","Campbell","Grant","Miller"})f.Admin.Save(null,f.Request(name));
        var first=f.Admin.Directory(new(Sort:"-jobs,name",Page:1,PageSize:3));
        var second=f.Admin.Directory(new(Sort:"-jobs,name",Page:2,PageSize:3));
        var third=f.Admin.Directory(new(Sort:"-jobs,name",Page:3,PageSize:3));
        Assert.Equal(7,first.GetProperty("total").GetInt32());
        var names=first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray()).Concat(third.GetProperty("items").EnumerateArray()).Select(c=>c.GetProperty("displayName").GetString()).ToArray();
        Assert.Equal(new[]{"Amara","Campbell","Dahlia","Grant","Miller","Nia","Zuri"},names);
    }
    public sealed class Fixture:IAsyncDisposable
    {
        private readonly string schema="crm_"+Guid.NewGuid().ToString("N");private readonly string original=Environment.GetEnvironmentVariable("STUDIO_TEST_POSTGRES")!;private NpgsqlDataSource source=null!;
        public StudioDatabase Db {get;private set;}=null!;public CrmStore Admin {get;private set;}=null!;public Guid Workspace {get;private set;}public Guid OtherWorkspace {get;private set;}
        public static ClaimsPrincipal User(string name,bool administrator=false)=>new(new ClaimsIdentity(administrator?[new("login_name",name),new(ClaimTypes.Role,"Administrator")]:[new("login_name",name)],"test"));
        public CrmClientRequest Request(string name)=>new(name,PrimaryWorkspaceId:Workspace);
        public static async Task<Fixture> Create()
        {
            var f=new Fixture();await using(var c=new NpgsqlConnection(f.original)){await c.OpenAsync();await using var q=new NpgsqlCommand("create schema "+f.schema,c);await q.ExecuteNonQueryAsync();}
            f.source=new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(f.original){SearchPath=f.schema}.ConnectionString).Build();f.Db=new(f.source);
            await f.Db.InitializeAsync();await new StudioFeatureDatabase(f.source).InitializeAsync();await new Aegis.Studio.Web.Messaging.StudioMessagingDatabase(f.source).InitializeAsync();
            new StudioEmployeeDirectoryStore(f.Db).EnsureSchema();var workspaces=new WorkspaceStore(f.Db);f.Workspace=workspaces.Create(new("Portraits","portrait@example.test","portrait",true,1)).Id;f.OtherWorkspace=workspaces.Create(new("Corporate","corporate@example.test","corporate",true,2)).Id;
            new Aegis.Studio.Web.WorkspaceJobs.WorkspaceJobStore(f.Db).EnsureSchema();new StudioClientStore(f.Db).Migrate();new FrontDeskBookingRequestStore(f.Db).EnsureSchema();new StudioQuoteStore(f.Db).Migrate();new StudioAccountsStore(f.Db).EnsureSchema();Aegis.Studio.Web.Versioning.StudioVersionSchema.EnsureCreated(f.Db);Aegis.Studio.Web.Media.MediaProductionStore.Migrate(f.Db);WorkflowSchema.Ensure(f.Db);
            CrmStore.Migrate(f.Db);var roles=new GlobalOperationalRoleStore(f.Db);roles.EnsureSchema();f.Admin=new(f.Db,User("admin",true));
            foreach(var name in new[]{"viewer","assistant"})f.Sql("insert into studio_workspace_members(workspace_id,user_name,display_name,is_active,access_workspace,can_view,created_at_utc,updated_at_utc) values($1,$2,$2,true,true,true,now(),now())",f.Workspace,name);
            return f;
        }
        public CrmStore For(string name,string[] permissions){var roles=new GlobalOperationalRoleStore(Db);var role=roles.CreateRole(new(name,null,permissions));roles.SaveMember(role.Id,new(name,name));return new(Db,User(name));}
        public Guid Job(Guid client)
        {
            Guid conversation=Guid.NewGuid(),job=Guid.NewGuid();Sql("insert into studio_conversations(id,workspace_id,subject,contact_name,contact_address,channel,client_id,created_at_utc) values($1,$2,'Portrait enquiry','Client','client@example.test','Email',$3,now())",conversation,Workspace,client);
            Sql("insert into studio_jobs(id,workspace_id,conversation_id,title,client_name,status,client_id,created_at_utc) values($1,$2,$3,'Portrait job','Client','Pending',$4,now())",job,Workspace,conversation,client);return job;
        }
        public void Sql(string sql,params object[] args){using var c=Db.OpenConnection();using var q=new NpgsqlCommand(sql,c);foreach(var arg in args)q.Parameters.AddWithValue(arg);q.ExecuteNonQuery();}
        public object? Scalar(string sql,params object[] args){using var c=Db.OpenConnection();using var q=new NpgsqlCommand(sql,c);foreach(var arg in args)q.Parameters.AddWithValue(arg);return q.ExecuteScalar();}
        public int Count(string table)=>Convert.ToInt32(Scalar("select count(*) from "+table));
        public async ValueTask DisposeAsync(){await source.DisposeAsync();await using var c=new NpgsqlConnection(original);await c.OpenAsync();await using var q=new NpgsqlCommand("drop schema "+schema+" cascade",c);await q.ExecuteNonQueryAsync();}
    }
}
