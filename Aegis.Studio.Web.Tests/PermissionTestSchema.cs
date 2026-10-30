using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Tests;

internal static class PermissionTestSchema
{
    // Permission-store fixtures need the workflow relations queried by JobAccessService,
    // but deliberately do not enable lifecycle/notification triggers or seed a workflow.
    public static void Ensure(StudioDatabase database)
    {
        using var connection=database.OpenConnection();
        using var command=new NpgsqlCommand("""
            create table if not exists studio_workflow_sessions(
                id uuid primary key,job_id uuid not null references studio_jobs(id),required boolean not null default true);
            create table if not exists studio_workflow_crew(
                session_id uuid not null references studio_workflow_sessions(id),user_name text not null,primary key(session_id,user_name));
            create table if not exists studio_job_owner_assignments(
                id uuid primary key,job_id uuid not null references studio_jobs(id),assigned_to text not null,status text not null);
            alter table studio_jobs add column if not exists owner_assignment_id uuid references studio_job_owner_assignments(id);
            """,connection);
        command.ExecuteNonQuery();
    }
}
