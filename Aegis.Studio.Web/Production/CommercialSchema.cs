using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Production;

public static class CommercialSchema
{
    public const string Sql = """
        create table if not exists studio_workflow_template_drafts(
          template_id uuid primary key,revision integer not null,definition jsonb not null,active boolean not null,
          base_version integer not null,updated_by text not null,updated_at timestamptz not null default now());
        create table if not exists studio_commercial_templates(
          id uuid primary key,kind text not null check(kind in ('Job','WorkOrder')),version integer not null,
          active boolean not null default true,definition jsonb not null,updated_by text not null,updated_at timestamptz not null default now());
        create table if not exists studio_commercial_template_versions(
          template_id uuid not null references studio_commercial_templates(id),version integer not null,definition jsonb not null,
          created_by text not null,created_at timestamptz not null default now(),primary key(template_id,version));
        alter table studio_pos_catalog add column if not exists description text not null default '';
        alter table studio_pos_catalog add column if not exists estimated_cost numeric(18,2) not null default 0;
        alter table studio_pos_catalog add column if not exists tax_rate numeric(5,2) not null default 0;
        alter table studio_pos_catalog add column if not exists options jsonb not null default '{}';
        alter table studio_pos_catalog add column if not exists requires_production boolean not null default false;
        alter table studio_pos_catalog add column if not exists work_order_template_id uuid null references studio_commercial_templates(id);
        alter table studio_pos_catalog add column if not exists bundle jsonb not null default '[]';
        alter table studio_pos_catalog add column if not exists price_list_key text not null default 'Standard';
        create table if not exists studio_catalog_line_snapshots(
          sale_line_id uuid primary key references studio_pos_sale_lines(id),catalog_item_id uuid not null references studio_pos_catalog(id),
          catalog_price numeric(18,2) not null,net_unit_price numeric(18,2) not null,tax_rate numeric(5,2) not null,
          estimated_cost numeric(18,2) not null,specifications text null,definition jsonb not null,requires_production boolean not null,
          template_id uuid null,template_version integer null,template_definition jsonb null,
          created_at timestamptz not null default now());
        create table if not exists studio_job_template_instances(
          job_id uuid primary key references studio_jobs(id),template_id uuid not null,template_version integer not null,
          definition jsonb not null,operation_id uuid not null,created_by text not null,created_at timestamptz not null default now());
        create table if not exists studio_job_catalog_agreements(
          id uuid primary key,job_id uuid not null references studio_jobs(id),catalog_item_id uuid not null references studio_pos_catalog(id),
          quantity integer not null, specifications text null,confirmed_unit_price numeric(18,2) null,definition jsonb null,
          confirmed_at timestamptz null,sale_line_id uuid null references studio_pos_sale_lines(id));
        alter table studio_job_catalog_agreements add column if not exists workflow_selected boolean not null default true;
        alter table studio_catalog_line_snapshots add column if not exists agreement_id uuid null references studio_job_catalog_agreements(id);
        create table if not exists studio_catalog_work_orders(
          id uuid primary key,job_id uuid not null references studio_jobs(id),catalog_item_id uuid not null references studio_pos_catalog(id),
          sale_line_id uuid not null references studio_pos_sale_lines(id),template_id uuid not null,template_version integer not null,
          template_definition jsonb not null,product_name text not null,quantity integer not null,original_quantity integer not null,
          specifications text null,asset_id uuid null references studio_media_assets(id),source_version_id uuid null references studio_asset_versions(id),
          exception_reason text null,created_at timestamptz not null default now(),unique(sale_line_id,catalog_item_id));
        alter table studio_job_activities add column if not exists work_order_id uuid null references studio_catalog_work_orders(id);
        alter table studio_job_activities add column if not exists template_step_key text null;
        alter table studio_job_activities add column if not exists production_area text null;
        alter table studio_job_activities add column if not exists step_definition jsonb null;
        alter table studio_job_activities add column if not exists quality_control_required boolean not null default false;
        alter table studio_job_activities add column if not exists optional_step boolean not null default false;
        alter table studio_job_activities add column if not exists approved_image_required boolean not null default false;
        alter table studio_job_activities add column if not exists step_order integer not null default 0;
        alter table studio_job_activities add column if not exists workflow_node_id text null;
        create unique index if not exists ux_job_workflow_node on studio_job_activities(job_id,workflow_node_id) where workflow_node_id is not null;
        create table if not exists studio_job_workflow_operations(
          job_id uuid not null references studio_jobs(id),operation_id uuid not null,payload jsonb not null,
          actor text not null,created_at timestamptz not null default now(),primary key(job_id,operation_id));
        create table if not exists studio_job_workflow_history(
          id uuid primary key,job_id uuid not null references studio_jobs(id),definition jsonb not null,
          template_id uuid not null,template_version integer not null,actor text not null,reason text not null,
          created_at timestamptz not null default now());
        create table if not exists studio_activity_dependencies(
          activity_id uuid not null references studio_job_activities(id),depends_on_id uuid not null references studio_job_activities(id),
          primary key(activity_id,depends_on_id),check(activity_id<>depends_on_id));
        create unique index if not exists ux_catalog_order_step on studio_job_activities(work_order_id,template_step_key) where work_order_id is not null;
        create index if not exists ix_catalog_order_job on studio_catalog_work_orders(job_id);
        create index if not exists ix_production_area_queue on studio_job_activities(production_area,status,due_at_utc,id) where work_order_id is not null;
        """;
    public static void Ensure(StudioDatabase database)
    {
        using var c = database.OpenConnection(); using var tx = c.BeginTransaction();
        using var cmd = new NpgsqlCommand(Sql, c, tx); cmd.ExecuteNonQuery(); tx.Commit();
    }
}
