using System.Security.Claims;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Scheduling;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class MyWorkStartupTests
{
    [Fact]
    public void RequestServicesCanBeConstructedWithoutDatabaseAccess()
    {
        using var source = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=1");
        var database = new StudioDatabase(source);
        // Constructing either service used to issue DDL on the request path.
        Assert.NotNull(new JobAccessService(database));
        Assert.NotNull(new ScheduleStore(database, new ConfigurationBuilder().Build()));
    }

    [Fact]
    public async Task StartupFailuresKeepTheirTraceAndPropagateTheOriginalException()
    {
        var context = new DefaultHttpContext { TraceIdentifier = "startup-test-trace" };
        context.Request.Path = "/api/my-work/jobs";
        context.Request.Headers["X-Studio-Startup"] = "startup-test-run";
        var failure = new TimeoutException("Query timed out");
        var middleware = new MyWorkStartupTimingMiddleware(_ => throw failure, NullLogger<MyWorkStartupTimingMiddleware>.Instance);
        Assert.Same(failure, await Assert.ThrowsAsync<TimeoutException>(() => middleware.InvokeAsync(context)));
        Assert.Equal("startup-test-trace", context.Response.Headers["X-Studio-Trace"].ToString());
    }

    [StudioPostgresFact]
    public async Task BulkAccessMatchesIndividualPermissionsForInheritanceOverridesAndGlobalRoles()
    {
        string schema = "studio_startup_test_" + Guid.NewGuid().ToString("N");
        string connectionString = Environment.GetEnvironmentVariable("STUDIO_TEST_POSTGRES")!;
        using var admin = new NpgsqlConnection(connectionString);
        admin.Open();
        Sql(admin, $"create schema {schema}");
        using var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema }.ConnectionString);
        var database = new StudioDatabase(source);
        try
        {
            await database.InitializeAsync();
            await new StudioFeatureDatabase(source).InitializeAsync();
            var roles = new GlobalOperationalRoleStore(database);
            roles.EnsureSchema();
            roles.EnsureDefaultUserRole("staff");
            var workspaces = new WorkspaceAccessService(database);
            var access = new JobAccessService(database);
            Guid permitted = Guid.NewGuid(), denied = Guid.NewGuid();
            Guid[] jobs = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
            using (var connection = database.OpenConnection())
            {
                foreach (Guid workspace in new[] { permitted, denied })
                    Sql(connection, "insert into studio_workspaces(id,name,created_at_utc,updated_at_utc) values($1,'Startup test',now(),now())", workspace);
                for (int i = 0; i < jobs.Length; i++)
                {
                    Guid workspace = i < 3 ? permitted : denied, conversation = Guid.NewGuid();
                    Sql(connection, "insert into studio_conversations(id,workspace_id,subject,contact_name,contact_address,channel,created_at_utc) values($1,$2,'Test','Client','client@example.test','Email',now())", conversation, workspace);
                    Sql(connection, "insert into studio_jobs(id,workspace_id,conversation_id,title,client_name,status,created_at_utc) values($1,$2,$3,'Test','Client','New',now())", jobs[i], workspace, conversation);
                }
            }
            workspaces.SaveMember(permitted, new("staff", "Staff", true, WorkspacePermissionSet.ViewOnly));
            workspaces.SaveMember(denied, new("staff", "Staff", false, WorkspacePermissionSet.ViewOnly));
            access.SaveMember(jobs[1], new("staff", "Staff", true, true, JobPermissionSet.None));
            access.SaveMember(jobs[2], new("staff", "Staff", true, false, JobPermissionSet.None));
            access.SaveMember(jobs[3], new("staff", "Staff", true, false, JobPermissionSet.Viewer with { ViewAttachments = false }));
            access.SaveMember(jobs[4], new("staff", "Staff", true, false, JobPermissionSet.Full));
            access.SaveMember(jobs[5], new("staff", "Staff", false, false, JobPermissionSet.Full));
            var staff = User("STAFF");
            Compare(staff);
            Assert.Equal(4, access.GetAccessibleJobIds(staff).Count);
            Assert.Equal(3, access.GetAccessibleJobIds(staff, true).Count);

            Guid managerRole = roles.GetRoles().Single(x => x.Name == StudioRoleNames.OperationsManager).Id;
            roles.SaveGroupMapping("Managers", managerRole);
            var manager = User("staff", new Claim("directory_group", "Managers"));
            Compare(manager);
            Assert.Equal(6, access.GetAccessibleJobIds(manager).Count);
            Assert.Equal(3, access.GetAccessibleJobIds(manager, true).Count);

            Guid frontDeskRole = roles.GetRoles().Single(x => x.Name == StudioRoleNames.FrontDesk).Id;
            roles.SaveMember(frontDeskRole, new("staff", "Staff"));
            Compare(staff);
            Assert.Equal(6, access.GetAccessibleJobIds(staff, true).Count);
            Compare(User("admin", new Claim(ClaimTypes.Role, "Administrator")));

            void Compare(ClaimsPrincipal user)
            {
                foreach (bool attachments in new[] { false, true })
                {
                    Guid[] expected = jobs.Where(id => { var p = access.GetPermissions(user, id); return p.AccessJob && (!attachments || p.ViewAttachments); }).Order().ToArray();
                    Assert.Equal(expected, access.GetAccessibleJobIds(user, attachments).Order().ToArray());
                }
            }
        }
        finally
        {
            source.Dispose();
            Sql(admin, $"drop schema {schema} cascade");
        }
    }

    private static ClaimsPrincipal User(string name, params Claim[] additional) => new(new ClaimsIdentity(new[] { new Claim("login_name", name) }.Concat(additional), "Test"));
    private static void Sql(NpgsqlConnection connection, string sql, params object[] values)
    {
        using var command = new NpgsqlCommand(sql, connection);
        foreach (object value in values) command.Parameters.AddWithValue(value);
        command.ExecuteNonQuery();
    }
}
