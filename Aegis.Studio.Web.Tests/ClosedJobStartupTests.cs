using Aegis.Studio.Web.Media;
using Aegis.Studio.Web.WorkDay;
using Aegis.Studio.Web.WorkspaceJobs;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Workflow;
using Npgsql;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class ClosedJobStartupTests
{
    [StudioPostgresFact]
    public async Task RestartPreservesExistingJobsAndKeepsGuardActive()
    {
        using var context = new IntegratedWorkflowTests.WorkflowContext();
        WorkflowSchema.Ensure(context.Database);
        using var source = CreateSource(context.Connection);
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

        // Neither initializer may rewrite existing jobs, including open jobs.
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
    [StudioPostgresFact]
    public async Task SchemaInitializationDoesNotConvertDeleteOrRecreateBusinessRecords()
    {
        using var context = new IntegratedWorkflowTests.WorkflowContext();
        WorkflowSchema.Ensure(context.Database);
        using (var setup = new NpgsqlCommand("""
            create table studio_production_tasks(id uuid primary key);
            insert into studio_production_tasks values(gen_random_uuid());
            alter table studio_shoot_sessions alter column schedule_entry_id drop not null;
            insert into studio_shoot_sessions(
                id,job_id,schedule_entry_id,photographer_user_name,call_time_utc,planned_end_utc,status,created_at_utc,updated_at_utc)
            values(gen_random_uuid(),$1,null,'photographer',now(),now()+interval '1 hour','Scheduled',now(),now());
            """, context.Connection))
        {
            setup.Parameters.AddWithValue(context.Job);
            setup.ExecuteNonQuery();
        }
        var tables = new[] {
            "studio_jobs", "studio_job_subjects", "studio_job_activities",
            "studio_media_galleries", "studio_media_collections", "studio_workflow_history",
            "studio_workflow_crew", "studio_job_owner_assignments", "studio_shoot_sessions",
            "studio_production_tasks"
        };
        var before = tables.ToDictionary(table => table, Count);
        using var source = CreateSource(context.Connection);
        for (int restart = 0; restart < 2; restart++)
        {
            await context.Database.InitializeAsync();
            await new StudioFeatureDatabase(source).InitializeAsync();
            new WorkspaceJobStore(context.Database).EnsureSchema();
            MediaProductionStore.Migrate(context.Database);
            new WorkDayStore(context.Database).EnsureSchema();
            WorkflowSchema.Ensure(context.Database);
        }
        foreach (string table in tables) Assert.Equal(before[table], Count(table));

        long Count(string table)
        {
            using var command = new NpgsqlCommand($"select count(*) from {table}", context.Connection);
            return (long)command.ExecuteScalar()!;
        }
    }

    private static NpgsqlDataSource CreateSource(NpgsqlConnection existingConnection)
    {
        using var schemaCommand = new NpgsqlCommand("select current_schema()", existingConnection);
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
        return builder.Build();

    }

}
