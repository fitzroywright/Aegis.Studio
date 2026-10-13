using System.Reflection;
using System.Security.Claims;
using Aegis.Studio.Web.FrontDesk;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Workflow;
using Common.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Npgsql;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class DemoIsolationTests
{
    [StudioPostgresFact]
    public async Task FailedStoragePurgeDoesNotLeaveBrokenBusinessReferencesVisible()
    {
        using var context=new IntegratedWorkflowTests.WorkflowContext();
        new GlobalOperationalRoleStore(context.Database).EnsureSchema();
        new StudioEmployeeDirectoryStore(context.Database).EnsureSchema();
        WorkflowSchema.Ensure(context.Database);
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>Clear(context.Database,new FailingBucket(),context.Root));
        Assert.Contains("business records were cleared",error.Message);
        Assert.Contains("storage cleanup did not complete",error.Message);
        Assert.Equal(0L,Scalar(context.Connection,"select count(*) from studio_jobs"));
        Assert.Equal(0L,Scalar(context.Connection,"select count(*) from studio_asset_versions"));
    }

    [StudioPostgresFact]
    public async Task ClearPreservesSecurityAndNormalBookingAndQuoteConversionWorkWithoutDemo()
    {
        using var context = new IntegratedWorkflowTests.WorkflowContext();
        var roles = new GlobalOperationalRoleStore(context.Database);
        roles.EnsureSchema();
        new StudioEmployeeDirectoryStore(context.Database).EnsureSchema();
        new FrontDeskBookingRequestStore(context.Database).EnsureSchema();
        new StudioQuoteStore(context.Database).Migrate();
        WorkflowSchema.Ensure(context.Database);
        var role = roles.GetRoles().Single(r => r.Name == StudioRoleNames.OperationsManager);
        roles.SaveGroupMapping("custom-studio-group", role.Id);
        roles.SaveMember(role.Id, new("normal.staff", "Normal Staff"));
        Sql(context.Connection, "delete from studio_global_operational_role_permissions where role_id=$1 and permission_key=$2", role.Id, StudioPermissions.NotifyJobMembers);
        roles.EnsureSchema();
        Assert.Equal(0L, Scalar(context.Connection, "select count(*) from studio_global_operational_role_permissions where role_id=$1 and permission_key=$2", role.Id, StudioPermissions.NotifyJobMembers));
        var identity = new ClaimsPrincipal(new ClaimsIdentity([new Claim("login_name", "normal.staff")], "test"));
        Assert.True(roles.HasPermission(identity, StudioPermissions.ViewAllJobs));
        long permissions = (long)Scalar(context.Connection, "select count(*) from studio_global_operational_role_permissions")!;

        var storage = new LocalFileStorage(context.Root);
        using (var content = new MemoryStream([1,2,3]))
            await storage.StoreAsync(new("broken-demo.jpg",content,"image/jpeg","broken-demo.jpg","demo"));
        File.Delete(Path.Combine(context.Root,"broken-demo.jpg"));
        await Clear(context.Database, storage, context.Root);
        Assert.Equal(0L, Scalar(context.Connection,"select count(*) from studio_jobs"));
        Assert.Equal(permissions, Scalar(context.Connection,"select count(*) from studio_global_operational_role_permissions"));
        Assert.Equal(1L, Scalar(context.Connection,"select count(*) from studio_directory_group_roles where group_name='custom-studio-group'"));
        Assert.True(roles.HasPermission(identity,StudioPermissions.ViewAllJobs));
        Assert.Empty(await ((IStorageQuery)storage).ListAsync(new StorageListRequest()));

        var workspace = new WorkspaceStore(context.Database).Create(new("Normal studio",null,"normal-studio",true,1));
        var client = new StudioClientStore(context.Database).Save(null,new("Real client","client@example.test","8765550100"));
        var bookings = new FrontDeskBookingRequestStore(context.Database);
        var booking = bookings.Create(new(workspace.Id,"Real client","client@example.test","Portrait",DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)),500,"JMD",null,ClientId:client.Id),"normal.staff");
        Assert.NotNull(booking.JobId);
        Assert.Equal("Pending",Scalar(context.Connection,"select status from studio_jobs where id=$1",booking.JobId!.Value));
        var quotes = new StudioQuoteStore(context.Database);
        Guid quote = quotes.Create(new(client.Id,workspace.Id,"Quoted portrait",DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)),null,
            new([new("Portrait session",1,1000)],0,DateTimeOffset.UtcNow.AddDays(7)),500),"normal.staff");
        quotes.Accept(quote,"Client accepted by telephone");
        Guid request = quotes.ConvertToBooking(quote,"normal.staff");
        Assert.IsType<Guid>(Scalar(context.Connection,"select job_id from studio_frontdesk_requests where id=$1",request));
        Assert.Equal(request,quotes.ConvertToBooking(quote,"normal.staff"));
        Assert.Equal(2L,Scalar(context.Connection,"select count(*) from studio_jobs"));
        Assert.Equal(0L,Scalar(context.Connection,"select count(*) from studio_state where key like 'demo-data-%'"));
    }

    [StudioPostgresFact]
    public async Task RepeatedStartupDoesNotChangeInvalidDemoRowsOrDeleteOrphanShoots()
    {
        using var context = new IntegratedWorkflowTests.WorkflowContext();
        new GlobalOperationalRoleStore(context.Database).EnsureSchema();
        new StudioEmployeeDirectoryStore(context.Database).EnsureSchema();
        WorkflowSchema.Ensure(context.Database);
        Sql(context.Connection,"create table studio_production_tasks(id uuid primary key)");
        Sql(context.Connection,"insert into studio_production_tasks values($1)",Guid.NewGuid());
        Sql(context.Connection,"update studio_jobs set status='Unsupported demo stage',assigned_to='legacy.person',job_lead=null where id=$1",context.Job);
        Sql(context.Connection,"alter table studio_shoot_sessions alter column schedule_entry_id drop not null");
        Sql(context.Connection,"""
            insert into studio_shoot_sessions(id,job_id,schedule_entry_id,photographer_user_name,call_time_utc,planned_end_utc,status,created_at_utc,updated_at_utc)
            values($1,$2,null,'demo.person',now(),now()+interval '1 hour','Scheduled',now(),now())
            """,Guid.NewGuid(),context.Job);
        long revision = (long)Scalar(context.Connection,"select workflow_revision from studio_jobs where id=$1",context.Job)!;
        for(int restart=0;restart<2;restart++)
        {
            await context.Database.InitializeAsync();
            var source=(NpgsqlDataSource)typeof(StudioDatabase).GetField("dataSource",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(context.Database)!;
            await new StudioFeatureDatabase(source).InitializeAsync();
            new Aegis.Studio.Web.WorkspaceJobs.WorkspaceJobStore(context.Database).EnsureSchema();
            Aegis.Studio.Web.Media.MediaProductionStore.Migrate(context.Database);
            new Aegis.Studio.Web.WorkDay.WorkDayStore(context.Database).EnsureSchema();
            WorkflowSchema.Ensure(context.Database);
        }
        Assert.Equal("Unsupported demo stage",Scalar(context.Connection,"select status from studio_jobs where id=$1",context.Job));
        Assert.Equal(DBNull.Value,Scalar(context.Connection,"select job_lead from studio_jobs where id=$1",context.Job));
        Assert.Equal(revision,Scalar(context.Connection,"select workflow_revision from studio_jobs where id=$1",context.Job));
        Assert.Equal(1L,Scalar(context.Connection,"select count(*) from studio_production_tasks"));
        Assert.Equal(1L,Scalar(context.Connection,"select count(*) from studio_shoot_sessions where schedule_entry_id is null"));
        Assert.Equal(0L,Scalar(context.Connection,"select count(*) from studio_job_owner_assignments"));
    }

    internal static Task<object> Clear(StudioDatabase database,IFileStorage storage,string root)
    {
        var configuration=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Storage:FactoryResetExclusiveRoot"]="true" }).Build();
        var type=typeof(WorkflowSchema).Assembly.GetType("Aegis.Studio.Web.Workflow.StudioFactoryReset")!;
        return (Task<object>)type.GetMethod("ExecuteAsync",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[database,storage,configuration,new TestEnvironment(root),null,CancellationToken.None])!;
    }
    private static void Sql(NpgsqlConnection connection,string sql,params object[] args)
    {using var command=new NpgsqlCommand(sql,connection);foreach(var arg in args)command.Parameters.AddWithValue(arg);command.ExecuteNonQuery();}
    private static object? Scalar(NpgsqlConnection connection,string sql,params object[] args)
    {using var command=new NpgsqlCommand(sql,connection);foreach(var arg in args)command.Parameters.AddWithValue(arg);return command.ExecuteScalar();}
    private sealed class TestEnvironment(string root):IWebHostEnvironment
    {
        public string ApplicationName{get;set;}="Tests";public string EnvironmentName{get;set;}="Test";
        public string ContentRootPath{get;set;}=root;public string WebRootPath{get;set;}=root;
        public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider();public IFileProvider WebRootFileProvider{get;set;}=new NullFileProvider();
    }
    private sealed class FailingBucket:IFileStorage,IStorageBucketReset
    {
        public Task<long> PurgeBucketAsync(CancellationToken cancellationToken=default)=>Task.FromException<long>(new IOException("Object store unavailable after partial purge"));
        public Task<StoredFile> StoreAsync(StorageWriteRequest request,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string key,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public Task<StoredFile?> GetMetadataAsync(string key,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public Task<IReadOnlyList<StoredFile>> GetVersionsAsync(string key,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public Task DeleteAsync(string key,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public Task<StorageHealth> CheckHealthAsync(CancellationToken cancellationToken=default)=>throw new NotSupportedException();
    }
}
