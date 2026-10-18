using Aegis.Studio.Web.FrontDesk;
using Aegis.Studio.Web.Media;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Production;
using Aegis.Studio.Web.Pos;
using Aegis.Studio.Web.WorkDay;
using Aegis.Studio.Web.Scheduling;
using Microsoft.Extensions.Configuration;
using Aegis.Pos.Core;
using Aegis.Studio.Web.Versioning;
using Aegis.Studio.Web.WorkspaceJobs;
using Common.Storage;
using Npgsql;
using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using SkiaSharp;

namespace Aegis.Studio.Web.Tests;
public sealed class IntegratedWorkflowTests
{
    [Fact]
    public void PhotoTagsAreNormalizedAndBounded()
    {
        Assert.Equal(new[]{"Portrait","Blue backdrop"},MediaProductionStore.NormalizeTags(new[]{" Portrait ","portrait","","Blue backdrop"}));
        Assert.Throws<ArgumentException>(()=>MediaProductionStore.NormalizeTags(new[]{new string('x',81)}));
        Assert.Throws<ArgumentException>(()=>MediaProductionStore.NormalizeTags(Enumerable.Range(0,51).Select(n=>"Tag "+n)));
    }
    [StudioPostgresFact]
    public void PhotoTagsPersistAndLegacyAssetUpdatesPreserveThem()
    {
        using var context=new WorkflowContext();Guid asset=Guid.NewGuid(),version=Guid.NewGuid();
        Sql(context.Connection,"insert into studio_media_galleries(id) values($1)",context.Job);
        Sql(context.Connection,"insert into studio_asset_versions(id,job_id,asset_key,version_number,file_name,content_type,size_bytes,sha256,stored_path,created_by,created_at_utc) values($1,$2,'tag-test',1,'Portrait.jpg','image/jpeg',1024,'test','tag-test','Test',now())",version,context.Job);
        Sql(context.Connection,"insert into studio_media_assets(id,job_id,gallery_id,asset_key) values($1,$2,$2,'tag-test')",asset,context.Job);
        var store=new MediaProductionStore(context.Database,context.Versions);
        store.UpdateAsset(context.Job,asset,new("Needs Sorting","Unclassified",null,null,Tags:[" Family ","family","Blue backdrop"]),"Test");
        string[] ReadTags()=>new MediaProductionStore(context.Database,context.Versions).Snapshot(context.Job,new(AssetId:asset)).GetProperty("assets")[0].GetProperty("tags").EnumerateArray().Select(value=>value.GetString()!).ToArray();
        Assert.Equal(new[]{"Family","Blue backdrop"},ReadTags());
        store.UpdateAsset(context.Job,asset,new("Needs Sorting","Unclassified",null,null),"Test");Assert.Equal(new[]{"Family","Blue backdrop"},ReadTags());
        store.UpdateAsset(context.Job,asset,new("Needs Sorting","Unclassified",null,null,Tags:[]),"Test");Assert.Empty(ReadTags());
    }
    [StudioPostgresFact]
    public void JobNotesPersistWithAuthorshipAndDoNotDuplicateOnRetry()
    {
        using var context=new WorkflowContext();
        var store=new WorkspaceJobStore(context.Database);
        Assert.Empty(store.Notes(context.Job));
        Guid id=Guid.NewGuid();
        var request=new JobNoteRequest(id,"  Confirm the client's print sizes.  ");
        Assert.Equal(id,store.AddNote(context.Job,request,"studio.manager"));
        Assert.Equal(id,store.AddNote(context.Job,request,"studio.manager"));
        var note=Assert.Single(new WorkspaceJobStore(context.Database).Notes(context.Job));
        Assert.Equal("Confirm the client's print sizes.",note.Body);
        Assert.Equal("studio.manager",note.CreatedBy);
        Assert.NotEqual(default,note.CreatedAtUtc);
        Assert.Throws<ArgumentException>(()=>store.AddNote(context.Job,new(id,"Different note"),"studio.manager"));
        Assert.Throws<ArgumentException>(()=>store.AddNote(context.Job,request,"different.user"));
        Guid otherJob=Guid.NewGuid();
        Sql(context.Connection,"insert into studio_jobs(id,workspace_id,conversation_id,title,client_name,status,created_at_utc) select $1,workspace_id,conversation_id,'Other job',client_name,status,now() from studio_jobs where id=$2",otherJob,context.Job);
        Assert.Throws<ArgumentException>(()=>store.AddNote(otherJob,request,"studio.manager"));
        Assert.Empty(store.Notes(otherJob));
        Assert.Throws<ArgumentException>(()=>store.AddNote(context.Job,new(Guid.NewGuid()," "),"studio.manager"));
        Assert.Throws<ArgumentException>(()=>store.AddNote(context.Job,new(Guid.Empty,"Note"),"studio.manager"));
        Assert.Throws<ArgumentException>(()=>store.AddNote(context.Job,new(Guid.NewGuid(),new string('x',10001)),"studio.manager"));
        Assert.Throws<UnauthorizedAccessException>(()=>store.UpdateNote(context.Job,id,new(1,"Not my note"),"another.user",false));
        Assert.True(store.UpdateNote(context.Job,id,new(1,"Updated print sizes."),"studio.manager",false));
        Assert.True(store.UpdateNote(context.Job,id,new(1,"Updated print sizes."),"studio.manager",false));
        var edited=Assert.Single(store.Notes(context.Job));
        Assert.Equal(2,edited.Revision);Assert.Equal("Updated print sizes.",edited.Body);
        Assert.Equal(note.CreatedBy,edited.CreatedBy);Assert.Equal(note.CreatedAtUtc,edited.CreatedAtUtc);
        Assert.Equal("studio.manager",edited.UpdatedBy);
        Assert.Throws<InvalidOperationException>(()=>store.UpdateNote(context.Job,id,new(1,"Stale update"),"studio.manager",true));
        Assert.False(store.UpdateNote(otherJob,id,new(2,"Wrong job"),"studio.manager",true));
        using var history=new NpgsqlCommand("select body from studio_job_note_revisions where note_id=$1 and revision=1",context.Connection);
        history.Parameters.AddWithValue(id);Assert.Equal(note.Body,history.ExecuteScalar());
    }
    [Fact] public void PhotoCatalogueRetainsReviewedSourcesAndLicensing()
    {
        var photos=DemoPortraitCatalogue.Photos;
        Assert.Equal(2,photos.Count);
        Assert.Equal(photos.Count,photos.Select(p=>p.Url).Distinct().Count());
        Assert.All(photos,p=>{Assert.Contains(p.License,new[]{"Public domain","CC0 1.0"});Assert.False(string.IsNullOrWhiteSpace(p.Credit));Assert.StartsWith("https://",p.Url);Assert.StartsWith("https://commons.wikimedia.org/wiki/File:",p.Page);});
        Assert.Throws<ArgumentOutOfRangeException>(()=>DemoPortraitCatalogue.Url(photos.Count));
    }
    [Fact] public void InvalidBulkAndQcInputsAreRejected()
    {
        Assert.Throws<ArgumentException>(()=>new MediaPageQuery(Limit:201).Validate());
        Assert.Throws<ArgumentException>(()=>new MediaPageQuery(SubjectSearch:new string('x',201)).Validate());
        Assert.Throws<ArgumentException>(()=>ProductionItems.Validate(new(1,"Rejected",null)));
        Assert.Throws<ArgumentException>(()=>ProductionItems.Validate(new(-1,"Passed",null)));
        Assert.Throws<ArgumentException>(()=>StudioFulfillment.Validate(new(Guid.Empty,"Collected","Client")));
        ProductionItems.Validate(new(3,"Rejected","Print colour needs rework"));
    }
    [StudioPostgresFact]
    public void HundredThousandAssetsUseBoundedPagesAndSqlSelections()
    {
        using var context=new WorkflowContext();
        var db=context.Database;var c=context.Connection;var versions=context.Versions;var job=context.Job;
        Sql(c,"insert into studio_media_galleries(id) values($1)",job);
        Sql(c,"""
            insert into studio_asset_versions(id,job_id,asset_key,version_number,file_name,content_type,size_bytes,sha256,stored_path,created_by,created_at_utc)
            select md5('version:'||n)::uuid,$1,'asset-'||n,1,'Photo '||n||'.jpg','image/jpeg',1,'test-metadata','scale/'||n,'Scale test',now() from generate_series(1,100000) n;
            insert into studio_media_assets(id,job_id,gallery_id,asset_key)
            select md5('asset:'||n)::uuid,$1,$1,'asset-'||n from generate_series(1,100000) n;
            insert into studio_job_asset_collections(job_id,asset_key,collection_name)
            select $1,'asset-'||n,case when n%2=0 then 'Even' else 'Odd' end from generate_series(1,100000) n
            """,job);
        var media=new MediaProductionStore(db,versions);var first=media.Snapshot(job);Assert.Equal(100000,first.GetProperty("totalAssets").GetInt32());Assert.Equal(100,first.GetProperty("assets").GetArrayLength());
        Assert.Equal(50000,first.GetProperty("collectionCounts").GetProperty("Even").GetInt32());
        Assert.Equal(50000,first.GetProperty("collectionCounts").GetProperty("Odd").GetInt32());
        Guid cursor=first.GetProperty("assets")[99].GetProperty("id").GetGuid();var second=media.Snapshot(job,new(After:cursor));
        Assert.Empty(first.GetProperty("assets").EnumerateArray().Select(a=>a.GetProperty("id").GetGuid()).Intersect(second.GetProperty("assets").EnumerateArray().Select(a=>a.GetProperty("id").GetGuid())));
        Assert.Equal(50000,media.Snapshot(job,new(Collection:"Even")).GetProperty("totalAssets").GetInt32());
        Guid subject=Guid.NewGuid();Sql(c,"insert into studio_job_subjects(id,job_id,gallery_id,name,reference,updated_by) values($1,$2,$2,'Batch subject','B1','Test')",subject,job);
        Assert.Equal(50000,MediaBulkSelection.Apply(db,job,new(true,null,new(Collection:"Even"),subject),"Manager"));
        Assert.Equal(50000,media.Snapshot(job,new(SubjectId:subject)).GetProperty("totalAssets").GetInt32());
    }
    [StudioPostgresFact]
    public void ApprovedV3SurvivesV4AndMultiLineOrderRefundReplay()
    {
        using var context=new WorkflowContext();
        var db=context.Database;var c=context.Connection;var versions=context.Versions;var job=context.Job;
        // A real stored source, rather than the stress fixture's metadata-only records.
        var v1=versions.Add(job,"approved-portrait","portrait.jpg","image/jpeg",new MemoryStream([1]),"Editor");
        var v2=versions.Add(job,"approved-portrait","portrait.jpg","image/jpeg",new MemoryStream([2]),"Editor",sourceVersionId:v1.Id);
        var v3=versions.Add(job,"approved-portrait","portrait.jpg","image/jpeg",new MemoryStream([3]),"Editor",sourceVersionId:v2.Id);
        Guid batch;using(var tx=c.BeginTransaction()){batch=ProductionItems.CreateApprovedBatch(c,tx,job,"Manager");tx.Commit();}
        var v4=versions.Add(job,"approved-portrait","portrait.jpg","image/jpeg",new MemoryStream([4]),"Editor",sourceVersionId:v3.Id);
        Assert.NotEqual(v3.Id,v4.Id);
        using var check=new NpgsqlCommand("select i.source_version_id from studio_production_items i join studio_media_assets a on a.id=i.asset_id where i.activity_id=$1 and a.asset_key='approved-portrait' and a.approved_version_id=$2",c);check.Parameters.AddWithValue(batch);check.Parameters.AddWithValue(v3.Id);Assert.Equal(v3.Id,check.ExecuteScalar());
        using var pinned=versions.Open(job,v3.Id,out _);Assert.Equal(3,pinned.ReadByte());
        Guid asset;using(var image=new NpgsqlCommand("select id from studio_media_assets where job_id=$1 and asset_key='approved-portrait'",c)){image.Parameters.AddWithValue(job);asset=(Guid)image.ExecuteScalar()!;}
        var studio=new StudioStore(db,new WorkspaceStore(db));var pos=new StudioPosStore(db,new PosAuditStore(db));
        var sale=pos.Create(job,new([new("PRINT-5X7","Print",1500,3,asset),new("PRINT-5X7","Second print line",1500,2,asset)],Guid.NewGuid()),studio,"Cashier",allowPriceOverride:true);
        pos.Complete(sale.Id,new([new(TenderKind.Cash,7500,"Test")],"Test terminal"),studio,"Cashier");
        var purchasedLines=sale.Lines.ToArray();Assert.Equal(2,purchasedLines.Length);
        using(var order=new NpgsqlCommand("select source_version_id from studio_production_items where source_sale_line_id=$1",c)){order.Parameters.AddWithValue(purchasedLines[0].Id);Assert.Equal(v3.Id,order.ExecuteScalar());}
        Guid refundOperation=Guid.NewGuid();var refund=new RefundStudioSaleRequest([new(purchasedLines[0].Id,1500,1)],[new(TenderKind.Cash,1500,"Test refund")],"Cancel one print",refundOperation);
        pos.Refund(sale.Id,refund,studio,"Cashier");pos.Refund(sale.Id,refund,studio,"Cashier");
        Assert.Single(pos.Get(sale.Id)!.Refunds);
        using(var order=new NpgsqlCommand("select quantity from studio_production_items where source_sale_line_id=$1",c)){order.Parameters.AddWithValue(purchasedLines[0].Id);Assert.Equal(2,order.ExecuteScalar());}
        using(var secondLine=new NpgsqlCommand("select quantity,source_version_id from studio_production_items where source_sale_line_id=$1",c))
        {
            secondLine.Parameters.AddWithValue(purchasedLines[1].Id);
            using var row=secondLine.ExecuteReader();Assert.True(row.Read());Assert.Equal(2,row.GetInt32(0));Assert.Equal(v3.Id,row.GetGuid(1));
        }
        Assert.Equal(6000m,studio.GetJob(job)!.FinancialEvents!.Where(e=>e.Kind is "POS Sale" or "POS Refund").Sum(e=>e.Amount));
    }
    [StudioPostgresFact]
    public async Task PreviewGenerationRetainsUnsupportedOriginals()
    {
        using var context=new WorkflowContext();
        var db=context.Database;var c=context.Connection;var versions=context.Versions;var job=context.Job;var root=context.Root;
        var v3=versions.Add(job,"unsupported-original","portrait.raw","application/octet-stream",new MemoryStream([3]),"Editor");
        using var originalPhoto=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"Fixtures","portrait.jpg"));var realPhoto=versions.Add(job,"real-preview","portrait.jpg","image/jpeg",originalPhoto,"Editor");
        var worker=new MediaDerivativeWorker(db,new LocalFileStorage(root),new TestEnvironment(root),NullLogger<MediaDerivativeWorker>.Instance);
        var generate=typeof(MediaDerivativeWorker).GetMethod("Generate",BindingFlags.Instance|BindingFlags.NonPublic)!;
        await (Task)generate.Invoke(worker,[realPhoto.Id,CancellationToken.None])!;
        using(var state=new NpgsqlCommand("select status,thumbnail_key from studio_media_derivatives where version_id=$1",c)){state.Parameters.AddWithValue(realPhoto.Id);using var row=state.ExecuteReader();Assert.True(row.Read());Assert.Equal("Ready",row.GetString(0));using var thumbnail=new LocalFileStorage(root).OpenReadAsync(row.GetString(1)).GetAwaiter().GetResult();using var image=SKBitmap.Decode(thumbnail);Assert.NotNull(image);Assert.True(Math.Max(image.Width,image.Height)<=384);}
        await (Task)generate.Invoke(worker,[v3.Id,CancellationToken.None])!;
        using(var state=new NpgsqlCommand("select status from studio_media_derivatives where version_id=$1",c)){state.Parameters.AddWithValue(v3.Id);Assert.Equal("Unsupported",state.ExecuteScalar());}
        using(var intact=versions.Open(job,v3.Id,out _))Assert.Equal(3,intact.ReadByte());
    }
    [StudioPostgresFact]
    public void DemoUpgradePreservesProductionLinksAcrossRetries()
    {
        using var context=new WorkflowContext();
        var c=context.Connection;var versions=context.Versions;var workspace=context.Workspace;var root=context.Root;
        Guid demoJob=Guid.Parse("de000000-0000-4000-0041-000000000101"),demoConversation=Guid.NewGuid();
        Sql(c,"insert into studio_conversations(id,workspace_id,subject,contact_name,contact_address,channel,created_at_utc) values($1,$2,'Demo approval','Client','client@example.test','Email',now())",demoConversation,workspace);
        Sql(c,"insert into studio_jobs(id,workspace_id,conversation_id,title,client_name,status,created_at_utc) values($1,$2,$3,'Demo approval','Client','In Production',now())",demoJob,workspace,demoConversation);
        using var demoPhoto=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"Fixtures","portrait.jpg"));var approvedDemo=versions.Add(demoJob,"demo-full/101/asset-001","portrait.jpg","image/jpeg",demoPhoto,"Editor","Real CC0/public-domain photograph from Wikimedia Commons. Seed fixture.");
        Sql(c,"update studio_media_assets set approved_version_id=$2 where job_id=$1",demoJob,approvedDemo.Id);
        var upgrade=typeof(DemoDataEndpoints).Assembly.GetType("Aegis.Studio.Web.Operations.DemoIntegratedWorkflow")!.GetMethod("Seed",BindingFlags.Static|BindingFlags.NonPublic)!;
        using(var tx=c.BeginTransaction()){upgrade.Invoke(null,[c,tx,"Manager",new LocalFileStorage(root),null]);tx.Commit();}
        using(var tx=c.BeginTransaction()){upgrade.Invoke(null,[c,tx,"Manager",new LocalFileStorage(root),null]);tx.Commit();}
        using(var saleLink=new NpgsqlCommand("select count(*),sum(i.quantity) from studio_production_items i join studio_pos_sale_lines l on l.id=i.source_sale_line_id join studio_pos_sales s on s.id=l.sale_id where s.reference_id=$1",c)){saleLink.Parameters.AddWithValue(demoJob.ToString("D"));using var r=saleLink.ExecuteReader();Assert.True(r.Read());Assert.Equal(1,r.GetInt64(0));Assert.Equal(2,r.GetInt64(1));}
    }
    [StudioPostgresFact]
    public void WorkDayReschedulePreservesIdsAndRollsBackConflicts()
    {
        using var context=new WorkflowContext();
        var db=context.Database;var job=context.Job;
        var photographers=new PhotographerStore(db);photographers.Save(new("a","Photographer A","Employee",true,[],null));photographers.Save(new("b","Photographer B","Employee",true,[],null));
        Sql(context.Connection,"create table studio_job_owner_assignments(id uuid primary key,status text);alter table studio_jobs add column owner_assignment_id uuid;insert into studio_job_owner_assignments values($1,'Accepted');update studio_jobs set owner_assignment_id=$1,status='Scheduled' where id=$2",Guid.NewGuid(),job);
        var scheduler=new WorkDaySchedulingService(db,photographers);var starts=DateTimeOffset.UtcNow.AddDays(5);
        var shoot=scheduler.Create(job,new("a",null,starts,starts.AddHours(1),"Studio",null,null,[]),"Manager");
        var moved=scheduler.Reschedule(job,shoot.Id,new("b",null,starts.AddHours(2),starts.AddHours(3),"Studio",null,null,[]),"Manager");
        Assert.Equal(shoot.Id,moved.Id);Assert.Equal(shoot.ScheduleEntryId,moved.ScheduleEntryId);Assert.Equal("b",moved.PhotographerUserName);
        var conflicting=scheduler.Create(job,new("a",null,starts.AddHours(2),starts.AddHours(3),"Studio",null,null,[]),"Manager");
        Assert.Throws<InvalidOperationException>(()=>scheduler.Reschedule(job,conflicting.Id,new("b",null,starts.AddHours(2),starts.AddHours(3),"Studio",null,null,[]),"Manager"));
        scheduler.Cancel(job,conflicting.Id,"Manager");scheduler.Cancel(job,conflicting.Id,"Manager");
        Assert.Equal("Cancelled",new OperationalWorkStore(db).GetShoots(job).Single(s=>s.Id==conflicting.Id).Status);
        Assert.Equal("a",new OperationalWorkStore(db).GetShoots(job).Single(s=>s.Id==conflicting.Id).PhotographerUserName);
    }
    [StudioPostgresFact]
    public void PartialBalancePaymentAndRefundUseSharedFinanceRecords()
    {
        using var context=new WorkflowContext();
        var studio=new StudioStore(context.Database,new WorkspaceStore(context.Database));
        var pos=new StudioPosStore(context.Database,new PosAuditStore(context.Database));
        studio.AddFinancialEvent(context.Job,new("Charge",10000m,"Package"));
        var sale=pos.Create(context.Job,new([new("BALANCE","Partial payment",2500m,1)],Guid.NewGuid(),true),studio,"Cashier");
        var completion=new CompleteStudioSaleRequest([new(TenderKind.Cash,2500m,"Partial")],"Counter");
        pos.Complete(sale.Id,completion,studio,"Cashier");pos.Complete(sale.Id,completion,studio,"Cashier");
        var job=studio.GetJob(context.Job)!;
        Assert.Single(job.FinancialEvents!,e=>e.Id==sale.Id);
        Assert.Equal(7500m,job.FinancialEvents!.Where(e=>e.Kind=="Charge").Sum(e=>e.Amount)-job.FinancialEvents!.Where(e=>e.Kind is "Payment" or "Refund").Sum(e=>e.Amount));
        var operation=Guid.NewGuid();
        var refund=new RefundStudioSaleRequest([new(sale.Lines.Single().Id,2500m,1)],[new(TenderKind.Cash,2500m,"Refund")],"Client cancelled",operation);
        pos.Refund(sale.Id,refund,studio,"Cashier");pos.Refund(sale.Id,refund,studio,"Cashier");
        job=studio.GetJob(context.Job)!;
        Assert.Single(job.FinancialEvents!,e=>e.Id==operation);
        Assert.Equal(0m,job.FinancialEvents!.Where(e=>e.Kind is "Payment" or "Refund").Sum(e=>e.Amount));
    }

    // Every scenario gets its own disposable schema and storage root; failures cannot
    // prevent unrelated workflow scenarios from running or use operational data.
    internal sealed class WorkflowContext : IDisposable
    {
        private readonly string schema="studio_workflow_"+Guid.NewGuid().ToString("N");
        private readonly NpgsqlConnection admin;
        private readonly NpgsqlDataSource source;
        public string Root {get;}
        public StudioDatabase Database {get;}
        public NpgsqlConnection Connection {get;}
        public StudioVersionStore Versions {get;}
        public Guid Job {get;}=Guid.NewGuid();
        public Guid Workspace {get;}=Guid.NewGuid();
        public WorkflowContext()
        {
            string connectionString=Environment.GetEnvironmentVariable("STUDIO_TEST_POSTGRES")!;
            Root=Path.Combine(Path.GetTempPath(),schema);
            admin=new NpgsqlConnection(connectionString);admin.Open();Sql(admin,$"create schema {schema}");
            var builder=new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(connectionString){SearchPath=schema}.ConnectionString);
            // Explicitly initialize physical connections too: embedded protocol servers may ignore startup options.
            builder.UsePhysicalConnectionInitializer(connection=>
            {
                using var command=new NpgsqlCommand($"set search_path to {schema}",connection);command.ExecuteNonQuery();
            },async connection=>
            {
                await using var command=new NpgsqlCommand($"set search_path to {schema}",connection);await command.ExecuteNonQueryAsync();
            });
            source=builder.Build();
            Database=new StudioDatabase(source);
            Connection=Database.OpenConnection();
            try
            {
                Database.InitializeAsync().GetAwaiter().GetResult();new StudioFeatureDatabase(source).InitializeAsync().GetAwaiter().GetResult();
                new WorkspaceJobStore(Database).EnsureSchema();new StudioClientStore(Database).Migrate();new ScheduleStore(Database,new ConfigurationBuilder().Build()).EnsureOperationalSchema();
                Versions=new StudioVersionStore(Database,new LocalFileStorage(Root));MediaProductionStore.Migrate(Database);
                Sql(Connection,ProductionItems.Schema);Sql(Connection,ProductionOrderLinker.Schema);Sql(Connection,CommercialSchema.Sql);
                Guid conversation=Guid.NewGuid();
                Sql(Connection,"insert into studio_workspaces(id,name,created_at_utc,updated_at_utc) values($1,'Workflow',now(),now())",Workspace);
                Sql(Connection,"insert into studio_conversations(id,workspace_id,subject,contact_name,contact_address,channel,created_at_utc) values($1,$2,'Workflow','Client','client@example.test','Email',now())",conversation,Workspace);
                Sql(Connection,"insert into studio_jobs(id,workspace_id,conversation_id,title,client_name,status,created_at_utc) values($1,$2,$3,'Workflow','Client','In Production',now())",Job,Workspace,conversation);
            }
            catch {Dispose();throw;}
        }
        public void Dispose()
        {
            Connection.Dispose();source.Dispose();
            try {Sql(admin,$"drop schema {schema} cascade");}
            finally {admin.Dispose();if(Directory.Exists(Root))Directory.Delete(Root,true);}
        }
    }
    private sealed class TestEnvironment(string root):IWebHostEnvironment
    {
        public string ApplicationName{get;set;}="Tests";public string EnvironmentName{get;set;}="Test";public string ContentRootPath{get;set;}=root;public string WebRootPath{get;set;}=root;
        public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider();public IFileProvider WebRootFileProvider{get;set;}=new NullFileProvider();
    }
    private static void Sql(NpgsqlConnection c,string sql,params object[] values){using var cmd=new NpgsqlCommand(sql,c){CommandTimeout=120};if(values.Length>0&&sql.Contains(';')){foreach(string statement in sql.Split(';',StringSplitOptions.RemoveEmptyEntries))Sql(c,statement,values);return;}foreach(var value in values)cmd.Parameters.AddWithValue(value);cmd.ExecuteNonQuery();}
}




