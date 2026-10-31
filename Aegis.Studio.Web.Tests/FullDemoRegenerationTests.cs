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
        Sql(ctx.Connection,"insert into studio_worker_types(code,display_name,can_be_primary) values('CUSTOM_ROLE','Custom Role',false)");
        Sql(ctx.Connection,"insert into studio_state(key,value,updated_at_utc) values('demo-job-workflow-graphs-v1','seeded',now()),('provider-contract','preserved',now())");
        Sql(ctx.Connection,"create table studio_demo_seed_runs(run_id uuid primary key,status text)");
        Sql(ctx.Connection,"insert into studio_demo_seed_runs values($1,'Running'),($2,'Completed')",Guid.NewGuid(),Guid.NewGuid());
        ResetType.GetMethod("Reset",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[ctx.Database]);
        Assert.Equal(0,Scalar(ctx.Connection,"select count(*)::int from studio_jobs"));
        Assert.Equal(1,Scalar(ctx.Connection,"select count(*)::int from studio_workspaces where system_key='unassigned'"));
        Assert.Equal(0,Scalar(ctx.Connection,"select count(*)::int from studio_state where key like 'demo-job-workflow-graphs-%'"));
        Assert.Equal("preserved",Scalar(ctx.Connection,"select value from studio_state where key='provider-contract'"));
        Assert.Equal(1,Scalar(ctx.Connection,"select count(*)::int from studio_employees where user_name='kept.staff'"));
        Assert.Equal(1,Scalar(ctx.Connection,"select count(*)::int from studio_worker_types where code='CUSTOM_ROLE'"));
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
        Sql(ctx.Connection,"insert into studio_employees(user_name,display_name,is_active,is_assignable) values('demo.jason','Preserved staff account',false,false)");
        using(var old=new MemoryStream([1,2,3]))await storage.StoreAsync(new("old-demo.txt",old,"text/plain","old-demo.txt","Admin"));
        Sql(ctx.Connection,"insert into studio_state(key,value,updated_at_utc) values('demo-job-workflow-graphs-v1','old',now()),('provider-contract','preserved',now())");
        string? previousPhotoRoot=Environment.GetEnvironmentVariable("AEGIS_STUDIO_DEMO_PHOTOS");
        string photoRoot=Path.Combine(Path.GetTempPath(),"aegis-demo-photo-library-"+Guid.NewGuid().ToString("N"));
        byte[] photo=await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory,"Fixtures","portrait.jpg"));
        foreach(string category in new[]{"people","wedding","graduation","family","sports","product","staff"})
        {
            string directory=Path.Combine(photoRoot,category);Directory.CreateDirectory(directory);
            for(int i=0;i<4;i++)await File.WriteAllBytesAsync(Path.Combine(directory,$"fixture-{i}.jpg"),photo);
        }
        Environment.SetEnvironmentVariable("AEGIS_STUDIO_DEMO_PHOTOS",photoRoot);
        try
        {
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
            Assert.Equal(0,Scalar(ctx.Connection,"select count(*)::int from studio_jobs j where (select count(*) from studio_media_assets m where m.job_id=j.id)<1"));
            int assetCount=Convert.ToInt32(Scalar(ctx.Connection,"select count(*) from studio_media_assets"));
            Assert.True(assetCount>=126);
            Assert.Equal(4,Scalar(ctx.Connection,"select count(*)::int from studio_media_assets where job_id='de000000-0000-4000-0041-000000000106'"));
            var population=typeof(WorkflowSchema).Assembly.GetType("Aegis.Studio.Web.Operations.DemoJobPhotoPopulation")!;
            population.GetMethod("Seed",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[ctx.Database,storage,"Admin",null]);
            Assert.Equal(assetCount,Convert.ToInt32(Scalar(ctx.Connection,"select count(*) from studio_media_assets")));
            Assert.True(Convert.ToInt32(Scalar(ctx.Connection,"select count(*) from studio_photographers"))>=4);
            Assert.True(Convert.ToInt32(Scalar(ctx.Connection,"select count(*) from studio_frontdesk_requests"))>0);
            Assert.Equal(0,Scalar(ctx.Connection,"select count(*)::int from studio_frontdesk_requests r left join studio_jobs j on j.id=r.job_id where r.job_id is null or j.status<>'Pending'"));
            Assert.Equal(0,Scalar(ctx.Connection,"select count(*)::int from studio_frontdesk_requests r where coalesce((select sum(d.amount) from studio_frontdesk_deposits d where d.request_id=r.id),0)<>coalesce((select sum(f.amount) from studio_financial_events f where f.job_id=r.job_id and f.kind='Deposit'),0)"));
            Assert.Equal("Preserved staff account",Scalar(ctx.Connection,"select display_name from studio_employees where user_name='demo.jason' and not is_active and not is_assignable"));
            Assert.Equal(0,Scalar(ctx.Connection,"select count(*)::int from studio_jobs where status='Changes Requested' and (correction_kind is null or correction_kind not in ('Source','Production'))"));
            Assert.Equal(0,Scalar(ctx.Connection,"select count(*)::int from studio_job_subjects s where not s.is_system and nullif(trim(s.collection_name),'') is not null and not exists(select 1 from studio_media_collections c where c.job_id=s.job_id and c.name=s.collection_name)"));
            Assert.Equal(0,Scalar(ctx.Connection,"select count(*)::int from studio_job_subjects where collection_name='Roster'"));
            Assert.Equal(0,Scalar(ctx.Connection,"select count(*)::int from studio_media_assets a join studio_job_asset_collections c on c.job_id=a.job_id and c.asset_key=a.asset_key where (select count(*) from studio_media_asset_subjects s where s.asset_id=a.id)=1 and not exists(select 1 from studio_media_asset_subjects s join studio_job_subjects p on p.id=s.subject_id where s.asset_id=a.id and (coalesce(nullif(trim(p.collection_name),''),'Unclassified')=c.collection_name or a.asset_key not like 'demo-full/%' and a.asset_key not like 'demo-%/%'))"));
            Assert.Equal("Wedding Photography",Scalar(ctx.Connection,"select definition->>'name' from studio_job_template_instances where job_id='de000000-0000-4000-0041-000000000201'"));
            Assert.Equal("Corporate Headshots",Scalar(ctx.Connection,"select definition->>'name' from studio_job_template_instances where job_id='de000000-0000-4000-0041-000000000301'"));
            Assert.Equal("Graduation Photography",Scalar(ctx.Connection,"select definition->>'name' from studio_job_template_instances where job_id='de000000-0000-4000-0041-000000000401'"));
            Assert.Equal(0,Scalar(ctx.Connection,"select count(*)::int from studio_job_activities a join studio_jobs j on j.id=a.job_id where j.status in ('Active Production','Awaiting Approval','Changes Requested','Ready for Delivery','Delivered','Completed') and a.workflow_node_id in ('review','capture/shoot','capture/ingest') and a.status<>'Completed'"));
            Assert.Equal(0,Scalar(ctx.Connection,"select count(*)::int from studio_job_activities a where a.assigned_to like 'demo.%' and not exists(select 1 from studio_employees e where e.user_name=a.assigned_to)"));
            Assert.True(Convert.ToInt32(Scalar(ctx.Connection,"select count(*) from studio_employees where user_name like 'demo.%'"))>=13);
            Assert.Equal(0,Scalar(ctx.Connection,"select count(*)::int from studio_employees e where e.user_name like 'demo.%' and not exists(select 1 from studio_employee_photos p where lower(p.user_name)=lower(e.user_name) and p.source='DemoStaff')"));
            Assert.Equal(0,Scalar(ctx.Connection,"select count(*)::int from studio_employees e where e.user_name like 'demo.%' and e.is_active and not exists(select 1 from studio_worker_roles m join studio_worker_types r on r.code=m.role_code where m.user_name=e.user_name and m.is_active and r.is_active)"));
            Assert.True(Convert.ToInt32(Scalar(ctx.Connection,"select count(*) from (select user_name from studio_worker_roles where is_active and user_name like 'demo.%' group by user_name having count(*)>1) staff_with_multiple_roles"))>=3);
            Assert.Equal(0,Scalar(ctx.Connection,"select count(*)::int from studio_employees e where e.user_name like 'demo.%' and e.is_active and not exists(select 1 from studio_global_operational_role_members m join studio_global_operational_roles r on r.id=m.role_id join studio_global_operational_role_permissions p on p.role_id=r.id where m.user_name=e.user_name and m.is_active and r.is_active)"));
            Assert.Equal(0,Scalar(ctx.Connection,"select count(*)::int from studio_workspaces w where w.system_key is distinct from 'unassigned' and not exists(select 1 from studio_workspace_covers c where c.workspace_id=w.id and octet_length(c.image_data)>0)"));
            Assert.Equal(0,Scalar(ctx.Connection,"select count(*)::int from studio_workspace_covers c join studio_workspaces w on w.id=c.workspace_id where w.system_key='unassigned'"));
            Assert.Equal(13,Scalar(ctx.Connection,"select count(*)::int from studio_public_booking_hours"));
            Assert.Equal(2,Scalar(ctx.Connection,"select count(distinct location_code)::int from studio_public_booking_hours"));
            Assert.Equal(1,Scalar(ctx.Connection,"select count(*)::int from studio_public_booking_hours where location_code='BK' and day_of_week=0 and opens_at='10:00'::time and payload->>'closesAt'='15:00'"));
            Assert.Equal(1,Scalar(ctx.Connection,"select count(*)::int from studio_public_booking_hours where location_code='BE' and day_of_week=6 and opens_at='09:00'::time and payload->>'closesAt'='16:00'"));
            Assert.Equal(0,Scalar(ctx.Connection,"select count(*)::int from studio_public_booking_hours where payload->>'slotStepMinutes'<>'30'"));
            Assert.True(Convert.ToInt32(Scalar(ctx.Connection,"select count(*) from studio_job_subjects where job_id='de000000-0000-4000-0041-000000000201' and name='Emily Carter'"))>0);
        }
        finally{Environment.SetEnvironmentVariable("AEGIS_STUDIO_DEMO_PHOTOS",previousPhotoRoot);if(Directory.Exists(photoRoot))Directory.Delete(photoRoot,true);}
    }
    private class TestEnvironment(string root):IWebHostEnvironment
    {
        public string ApplicationName{get;set;}="Tests";public string EnvironmentName{get;set;}="Test";
        public string ContentRootPath{get;set;}=root;public string WebRootPath{get;set;}=root;
        public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider();public IFileProvider WebRootFileProvider{get;set;}=new NullFileProvider();
    }
}
