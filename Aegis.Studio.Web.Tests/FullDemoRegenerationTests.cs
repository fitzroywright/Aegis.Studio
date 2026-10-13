using System.Collections.Concurrent;
using System.Reflection;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Workflow;
using Common.Storage;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Aegis.Studio.Web.Tests;

[CollectionDefinition("FullStudioDemo",DisableParallelization=true)]
public class FullStudioDemoCollection { }

[Collection("FullStudioDemo")]
public class FullDemoRegenerationTests
{
    [StudioPostgresFact]
    public void StatusReadMarksAnInterruptedRunFailedWithoutChangingBusinessData()
    {
        using var ctx=new IntegratedWorkflowTests.WorkflowContext();
        var coordinator=new DemoSeedCoordinator(ctx.Database,new LocalFileStorage(ctx.Root),
            NullLogger<DemoSeedCoordinator>.Instance,new ConfigurationBuilder().Build(),new TestEnvironment(ctx.Root));
        Assert.Null(coordinator.Current);
        Guid id=Guid.NewGuid();
        Sql(ctx.Connection,"""
            insert into studio_demo_seed_runs(run_id,status,percent,stage,message,started_at_utc,updated_at_utc)
            values($1,'Running',42,'Studio photographs','Working',now(),now())
            """,id);
        var run=coordinator.Current!;
        Assert.True(run.Failed);Assert.Equal("Interrupted",run.Stage);Assert.Equal(42,run.Percent);
        Assert.True(coordinator.Get(id)!.Failed);
        Assert.Equal(1,Scalar(ctx.Connection,"select count(*)::int from studio_jobs"));
    }

    private static void Sql(NpgsqlConnection c,string sql,params object[] values){using var q=new NpgsqlCommand(sql,c);foreach(var v in values)q.Parameters.AddWithValue(v);q.ExecuteNonQuery();}
    private static object? Scalar(NpgsqlConnection c,string sql){using var q=new NpgsqlCommand(sql,c);return q.ExecuteScalar();}
    private static readonly Type DemoType=typeof(DemoDataEndpoints).Assembly.GetType("Aegis.Studio.Web.Operations.DemoDataExpansion")!;
    private static readonly Type ResetType=typeof(WorkflowSchema).Assembly.GetType("Aegis.Studio.Web.Workflow.ConnectedWorkflowDemo")!;

    [StudioPostgresFact] public void ResetClearsWorkflowSeedMarkersButPreservesConfigurationEmployeesAndRunningProgress()
    {
        using var ctx=new IntegratedWorkflowTests.WorkflowContext();WorkflowSchema.Ensure(ctx.Database);
        new GlobalOperationalRoleStore(ctx.Database).EnsureSchema();
        new StudioEmployeeDirectoryStore(ctx.Database).EnsureSchema();
        Sql(ctx.Connection,"insert into studio_employees(user_name,display_name) values('kept.staff','Kept Staff')");
        Sql(ctx.Connection,"insert into studio_state(key,value,updated_at_utc) values('demo-job-workflow-graphs-v1','seeded',now()),('provider-contract','preserved',now())");
        Sql(ctx.Connection,"create table studio_demo_seed_runs(run_id uuid primary key,status text)");
        Sql(ctx.Connection,"insert into studio_demo_seed_runs values($1,'Running'),($2,'Completed')",Guid.NewGuid(),Guid.NewGuid());
        ResetType.GetMethod("Reset",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[ctx.Database]);
        Assert.Equal(0,Scalar(ctx.Connection,"select count(*)::int from studio_jobs"));
        Assert.Equal(1,Scalar(ctx.Connection,"select count(*)::int from studio_workspaces where system_key='unassigned'"));
        Assert.Equal(0,Scalar(ctx.Connection,"select count(*)::int from studio_state where key like 'demo-job-workflow-graphs-%'"));
        Assert.Equal("preserved",Scalar(ctx.Connection,"select value from studio_state where key='provider-contract'"));
        Assert.Equal(1,Scalar(ctx.Connection,"select count(*)::int from studio_employees where user_name='kept.staff'"));
        Assert.Equal(1,Scalar(ctx.Connection,"select count(*)::int from studio_demo_seed_runs where status='Running'"));
        Assert.Equal(0,Scalar(ctx.Connection,"select count(*)::int from studio_demo_seed_runs where status='Completed'"));
    }

    [StudioPostgresFact] public Task FullResetRebuildsPhotographsTemplatesAndWorkflowScenarios()=>Rebuild(false);
    [StudioPostgresFact, Trait("Database","Native")] public Task NativeCoordinatorReportsFullResetCompletion()=>Rebuild(true);
    private static async Task Rebuild(bool tracked)
    {
        using var ctx=new IntegratedWorkflowTests.WorkflowContext();
        new Aegis.Studio.Web.FrontDesk.FrontDeskBookingRequestStore(ctx.Database).EnsureSchema();
        WorkflowSchema.Ensure(ctx.Database);StudioOperationsEnhancements.EnsureSchema(ctx.Database,new WorkspaceStore(ctx.Database));
        new Aegis.Studio.Web.WorkDay.WorkDayStore(ctx.Database).EnsureSchema();
        new StudioAccountsStore(ctx.Database).EnsureSchema();new ClientPortalStore(ctx.Database).EnsureSchema();
        Sql(ctx.Connection,"create table studio_job_fulfillment(operation_id uuid primary key,job_id uuid not null unique references studio_jobs(id),method text not null,recipient text not null,recorded_by text not null,recorded_at timestamptz not null default now())");
        new GlobalOperationalRoleStore(ctx.Database).EnsureSchema();
        new StudioEmployeeDirectoryStore(ctx.Database).EnsureSchema();
        var storage=new LocalFileStorage(ctx.Root);
        using(var old=new MemoryStream([1,2,3]))await storage.StoreAsync(new("old-demo.txt",old,"text/plain","old-demo.txt","Admin"));
        Sql(ctx.Connection,"insert into studio_state(key,value,updated_at_utc) values('demo-job-workflow-graphs-v1','old',now()),('provider-contract','preserved',now())");
        var cache=(ConcurrentDictionary<string,byte[]>)DemoType.GetField("weddingPhotoCache",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
        var original=cache.ToDictionary(x=>x.Key,x=>x.Value);
        byte[] photo=await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory,"Fixtures","portrait.jpg"));
        try
        {
            // Reuse the genuine repository photo fixture to test the complete pipeline without network downloads.
            foreach(var field in new[]{"realPhotoUrls","weddingPhotoUrls","productPhotoUrls","maternityPhotoUrls","eventPhotoUrls","birthdayPhotoUrls"})
            foreach(var url in (string[])DemoType.GetField(field,BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!)cache[url]=photo;
            foreach(var item in DemoPortraitCatalogue.Photos)cache[item.Url]=photo;
            var configuration=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Storage:FactoryResetExclusiveRoot","true"}}).Build();
            if(tracked)
            {
                var coordinator=new DemoSeedCoordinator(ctx.Database,storage,NullLogger<DemoSeedCoordinator>.Instance,configuration,new TestEnvironment(ctx.Root));
                var run=coordinator.Start("Admin");Assert.False(run.Completed);
                Assert.Throws<InvalidOperationException>(()=>coordinator.Start("Admin"));
                var deadline=DateTime.UtcNow.AddMinutes(5);
                do{await Task.Delay(100);run=coordinator.Get(run.RunId)!;}while(!run.Completed&&!run.Failed&&DateTime.UtcNow<deadline);
                Assert.True(run.Completed,run.Message);Assert.Equal(100,run.Percent);
            }
            else
            {
                var factory=typeof(WorkflowSchema).Assembly.GetType("Aegis.Studio.Web.Workflow.StudioFactoryReset")!;
                await (Task<object>)factory.GetMethod("ExecuteAsync",BindingFlags.Static|BindingFlags.NonPublic)!
                    .Invoke(null,[ctx.Database,storage,configuration,new TestEnvironment(ctx.Root),"Admin",CancellationToken.None])!;
            }
            Assert.False(File.Exists(Path.Combine(ctx.Root,"old-demo.txt")));
            Assert.Equal("preserved",Scalar(ctx.Connection,"select value from studio_state where key='provider-contract'"));
            Assert.Equal(8,Scalar(ctx.Connection,"select count(*)::int from studio_commercial_templates where kind='Job'"));
            Assert.Equal(Scalar(ctx.Connection,"select count(*)::int from studio_jobs"),Scalar(ctx.Connection,"select count(*)::int from studio_job_template_instances"));
            Assert.Equal(2,Scalar(ctx.Connection,"select count(*)::int from studio_commercial_templates where kind='WorkOrder'"));
            Assert.Equal(2,Scalar(ctx.Connection,"select count(*)::int from studio_pos_catalog where code like 'GRAPH-%' and requires_production and work_order_template_id is not null"));
            Assert.True(Convert.ToInt32(Scalar(ctx.Connection,"select count(*) from studio_jobs"))>=126);
            Assert.Equal(0,Scalar(ctx.Connection,"select count(*)::int from studio_jobs j where (select count(*) from studio_media_assets m where m.job_id=j.id)<40"));
            int assetCount=Convert.ToInt32(Scalar(ctx.Connection,"select count(*) from studio_media_assets"));
            Assert.True(assetCount>=126*40);
            Assert.Equal(180,Scalar(ctx.Connection,"select count(*)::int from studio_media_assets where job_id='de000000-0000-4000-0041-000000000106'"));
            var population=typeof(WorkflowSchema).Assembly.GetType("Aegis.Studio.Web.Operations.DemoJobPhotoPopulation")!;
            population.GetMethod("Seed",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[ctx.Database,storage,"Admin",null]);
            Assert.Equal(assetCount,Convert.ToInt32(Scalar(ctx.Connection,"select count(*) from studio_media_assets")));
            foreach(var item in new[]{(201,"Wedding"),(401,"Graduation"),(501,"Product"),(601,"Sports")})
            {
                var sources=(string[])DemoType.GetMethod("PhotoSources",BindingFlags.Static|BindingFlags.NonPublic)!
                    .Invoke(null,new object?[]{item.Item2,null})!;
                using var q=new NpgsqlCommand("select demo_source->>'source' from studio_asset_versions where job_id=$1",ctx.Connection);
                q.Parameters.AddWithValue(Guid.Parse($"de000000-0000-4000-0041-{item.Item1:000000000000}"));
                using var reader=q.ExecuteReader();
                int checkedPhotos=0;
                while(reader.Read()){Assert.Contains(reader.GetString(0),sources);checkedPhotos++;}
                Assert.True(checkedPhotos>=40);
            }
            Assert.True(Convert.ToInt32(Scalar(ctx.Connection,"select count(*) from studio_photographers"))>=4);
            Assert.True(Convert.ToInt32(Scalar(ctx.Connection,"select count(*) from studio_frontdesk_requests"))>0);
        }
        finally{cache.Clear();foreach(var item in original)cache[item.Key]=item.Value;}
    }
    private class TestEnvironment(string root):IWebHostEnvironment
    {
        public string ApplicationName{get;set;}="Tests";public string EnvironmentName{get;set;}="Test";
        public string ContentRootPath{get;set;}=root;public string WebRootPath{get;set;}=root;
        public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider();public IFileProvider WebRootFileProvider{get;set;}=new NullFileProvider();
    }
}
