using Aegis.Studio.Web.Operations;
using Npgsql;
using Xunit;
namespace Aegis.Studio.Web.Tests;

public sealed class DemoJobScopeTests
{
    [StudioPostgresFact]
    public void ConvertedDemoJobsSharePopulationScopeWhileUnrelatedJobsAreExcluded()
    {
        using var connection = new NpgsqlConnection(Environment.GetEnvironmentVariable("STUDIO_TEST_POSTGRES"));
        connection.Open();
        // Session-local fixtures shadow persistent tables without changing them.
        Run("create temporary table studio_jobs(id uuid primary key,title text,conversation_id uuid); create temporary table studio_frontdesk_requests(id uuid,job_id uuid,created_by text); create temporary table studio_bookings(id uuid,job_id uuid,external_booking_id text)");
        try
        {
            Guid seeded = Guid.Parse("de000000-0000-4000-0003-000000000001");
            Guid conversationJob = Guid.NewGuid(), bookingJob = Guid.NewGuid(), frontDeskJob = Guid.NewGuid(), realJob = Guid.NewGuid();
            Run("insert into studio_jobs values($1,'Seeded job',$2)", seeded, Guid.NewGuid());
            Run("insert into studio_jobs values($1,'Graduation Portrait',$2)", conversationJob, Guid.Parse("de000000-0000-4000-0002-000000000003"));
            Run("insert into studio_jobs values($1,'Converted demo booking',$2)", bookingJob, Guid.NewGuid());
            Run("insert into studio_jobs values($1,'Converted Front Desk request',$2)", frontDeskJob, Guid.NewGuid());
            Run("insert into studio_jobs values($1,'Graduation Portrait',$2)", realJob, Guid.NewGuid());
            Run("insert into studio_bookings values($1,$2,'DEMO-FULL-404')", Guid.NewGuid(), bookingJob);
            Run("insert into studio_frontdesk_requests values($1,$2,'demo-seed')", Guid.NewGuid(), frontDeskJob);
            var ids = DemoJobScope.Read(connection).Select(j => j.Id).ToHashSet();
            Assert.Equal(4, ids.Count);
            Assert.Contains(seeded, ids); Assert.Contains(conversationJob, ids); Assert.Contains(bookingJob, ids); Assert.Contains(frontDeskJob, ids);
            Assert.DoesNotContain(realJob, ids);
        }
        finally { Run("drop table pg_temp.studio_bookings,pg_temp.studio_frontdesk_requests,pg_temp.studio_jobs"); }
        void Run(string sql, params object[] values)
        { using var command = new NpgsqlCommand(sql, connection); foreach (var value in values) command.Parameters.AddWithValue(value); command.ExecuteNonQuery(); }
    }
}
