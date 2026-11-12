namespace Aegis.Studio.Web.SessionPlanning;

public static class PlanTemplateSchema
{
    public const string Sql = """
        create table if not exists studio_session_plan_templates(
          id uuid primary key, revision bigint not null, active boolean not null,
          definition jsonb not null, updated_by text not null, updated_at_utc timestamptz not null default now());
        create table if not exists studio_job_plan_structures(
          job_id uuid primary key references studio_jobs(id) on delete cascade,
          revision bigint not null, template_id uuid null, template_revision bigint null,
          definition jsonb not null, updated_by text not null, updated_at_utc timestamptz not null default now());
        """;
}
