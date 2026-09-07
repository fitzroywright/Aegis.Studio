using Npgsql;

namespace Aegis.Studio.Web.Persistence;

public sealed class StudioFeatureDatabase
{
    private readonly NpgsqlDataSource dataSource;

    public StudioFeatureDatabase(NpgsqlDataSource dataSource)
    {
        this.dataSource = dataSource;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            create table if not exists studio_notifications
            (
                id uuid primary key,
                user_name text not null,
                channel text not null,
                event_type text not null,
                subject text not null,
                body text not null,
                reference_type text null,
                reference_id text null,
                status text not null,
                destination text null,
                attempts integer not null default 0,
                last_error text null,
                is_read boolean not null default false,
                created_at_utc timestamptz not null,
                delivered_at_utc timestamptz null
            );
            create index if not exists ix_studio_notifications_user on studio_notifications(lower(user_name),created_at_utc desc);
            create index if not exists ix_studio_notifications_pending on studio_notifications(status,created_at_utc);

            create table if not exists studio_workspace_members
            (
                workspace_id uuid not null references studio_workspaces(id) on delete cascade,
                user_name text not null,
                display_name text not null,
                is_active boolean not null default true,
                access_workspace boolean not null default true,
                can_view boolean not null default true,
                reply_requester boolean not null default false,
                view_attachments boolean not null default true,
                add_attachments boolean not null default false,
                create_job boolean not null default false,
                edit_job boolean not null default false,
                manage_schedule boolean not null default false,
                manage_production boolean not null default false,
                notify_members boolean not null default false,
                edit_workspace boolean not null default false,
                manage_members boolean not null default false,
                admin_workspace boolean not null default false,
                created_at_utc timestamptz not null,
                updated_at_utc timestamptz not null,
                primary key(workspace_id,user_name)
            );
            create index if not exists ix_studio_workspace_members_user on studio_workspace_members(lower(user_name),workspace_id);

            create table if not exists studio_schedule_resources
            (
                id uuid primary key,
                name text not null,
                resource_type text not null,
                is_active boolean not null default true,
                notes text null,
                created_at_utc timestamptz not null
            );
            create unique index if not exists ux_studio_schedule_resources_name on studio_schedule_resources(lower(name),lower(resource_type));

            create table if not exists studio_schedule_entries
            (
                id uuid primary key,
                job_id uuid null references studio_jobs(id) on delete cascade,
                booking_id uuid null references studio_bookings(id) on delete set null,
                title text not null,
                starts_at_utc timestamptz not null,
                ends_at_utc timestamptz not null,
                location text null,
                status text not null,
                notes text null,
                created_at_utc timestamptz not null,
                check(ends_at_utc > starts_at_utc)
            );
            create index if not exists ix_studio_schedule_entries_range on studio_schedule_entries(starts_at_utc,ends_at_utc);
            create index if not exists ix_studio_schedule_entries_job on studio_schedule_entries(job_id);

            create table if not exists studio_schedule_entry_resources
            (
                entry_id uuid not null references studio_schedule_entries(id) on delete cascade,
                resource_id uuid not null references studio_schedule_resources(id),
                primary key(entry_id,resource_id)
            );
            create index if not exists ix_studio_schedule_entry_resources_resource on studio_schedule_entry_resources(resource_id,entry_id);

            create table if not exists studio_production_tasks
            (
                id uuid primary key,
                job_id uuid not null references studio_jobs(id) on delete cascade,
                title text not null,
                description text null,
                status text not null,
                assigned_to text null,
                due_at_utc timestamptz null,
                sort_order integer not null default 0,
                created_at_utc timestamptz not null,
                completed_at_utc timestamptz null
            );
            create index if not exists ix_studio_production_tasks_job on studio_production_tasks(job_id,sort_order);
            create index if not exists ix_studio_production_tasks_queue on studio_production_tasks(status,due_at_utc);

            create table if not exists studio_booking_api_nonces
            (
                client_id text not null,
                nonce text not null,
                used_at_utc timestamptz not null,
                primary key(client_id,nonce)
            );
            create index if not exists ix_studio_booking_api_nonces_used on studio_booking_api_nonces(used_at_utc);

            alter table studio_attachments add column if not exists sha256 text null;
            alter table studio_attachments add column if not exists uploaded_by text null;
            alter table studio_attachments add column if not exists storage_status text not null default 'Stored';
            create unique index if not exists ux_studio_attachments_stored_name on studio_attachments(stored_name);

            alter table studio_user_profiles add column if not exists theme_name text not null default 'studio-default';
            """;

        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
