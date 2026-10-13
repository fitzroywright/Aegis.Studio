using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Workflow;
using Npgsql;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class ClosedJobStartupTests
{
    [StudioPostgresFact]
    public async Task RestartPreservesJobLeadsAndKeepsGuardActive()
    {
        using var context = new IntegratedWorkflowTests.WorkflowContext();
        WorkflowSchema.Ensure(context.Database);
        using var schemaCommand = new NpgsqlCommand("select current_schema()", context.Connection);
        string schema = (string)schemaCommand.ExecuteScalar()!;
        var builder = new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("STUDIO_TEST_POSTGRES")!)
        { SearchPath = schema }.ConnectionString);
        builder.UsePhysicalConnectionInitializer(connection =>
        {
            using var command = new NpgsqlCommand($"set search_path to {schema}", connection);
            command.ExecuteNonQuery();
        }, async connection =>
        {
            await using var command = new NpgsqlCommand($"set search_path to {schema}", connection);
            await command.ExecuteNonQueryAsync();
        });
        using var source = builder.Build();
        var database = new StudioDatabase(source);
        var features = new StudioFeatureDatabase(source);
        var jobs = new Dictionary<string, Guid>();
        foreach (string status in new[] { "Completed", "Cancelled", "In Production" })
        {
            Guid id = Guid.NewGuid();
            jobs.Add(status, id);
            using var insert = new NpgsqlCommand("""
                insert into studio_jobs(id,workspace_id,conversation_id,title,client_name,status,created_at_utc,assigned_to,job_lead)
                select $1,workspace_id,conversation_id,'Restart test',client_name,$2,now(),'legacy-lead',null
                from studio_jobs where id=$3
                """, context.Connection);
            insert.Parameters.AddWithValue(id);
            insert.Parameters.AddWithValue(status);
            insert.Parameters.AddWithValue(context.Job);
            Assert.Equal(1, insert.ExecuteNonQuery());
        }

        // Startup must not populate or rewrite job leads on existing jobs.
        await features.InitializeAsync();
        for (int restart = 0; restart < 2; restart++)
        {
            await database.InitializeAsync();
            await features.InitializeAsync();
        }
        foreach (var job in jobs)
        {
            using var read = new NpgsqlCommand("select status,job_lead from studio_jobs where id=$1", context.Connection);
            read.Parameters.AddWithValue(job.Value);
            using var reader = read.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(job.Key, reader.GetString(0));
            Assert.True(reader.IsDBNull(1));
        }
        foreach (string status in new[] { "Completed", "Cancelled" })
        {
            using var edit = new NpgsqlCommand("update studio_jobs set title='Forbidden edit' where id=$1", context.Connection);
            edit.Parameters.AddWithValue(jobs[status]);
            var error = Assert.Throws<PostgresException>(() => edit.ExecuteNonQuery());
            Assert.Equal("55000", error.SqlState);
            Assert.Contains("Reopen the closed Job", error.MessageText);
        }
    }
}
