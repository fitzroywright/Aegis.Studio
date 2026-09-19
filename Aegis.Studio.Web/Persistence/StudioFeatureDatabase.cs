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
                id uuid primary key,user_name text not null,channel text not null,event_type text not null,subject text not null,body text not null,
                reference_type text null,reference_id text null,status text not null,destination text null,attempts integer not null default 0,
                last_error text null,is_read boolean not null default false,created_at_utc timestamptz not null,delivered_at_utc timestamptz null
            );
            create index if not exists ix_studio_notifications_user on studio_notifications(lower(user_name),created_at_utc desc);
            create index if not exists ix_studio_notifications_pending on studio_notifications(status,created_at_utc);

            create table if not exists studio_workspace_members
            (
                workspace_id uuid not null references studio_workspaces(id) on delete cascade,user_name text not null,display_name text not null,is_active boolean not null default true,
                access_workspace boolean not null default true,can_view boolean not null default true,reply_requester boolean not null default false,view_attachments boolean not null default true,
                add_attachments boolean not null default false,create_job boolean not null default false,edit_job boolean not null default false,manage_schedule boolean not null default false,
                manage_production boolean not null default false,notify_members boolean not null default false,edit_workspace boolean not null default false,manage_members boolean not null default false,
                admin_workspace boolean not null default false,send_as_self boolean not null default false,created_at_utc timestamptz not null,updated_at_utc timestamptz not null,primary key(workspace_id,user_name)
            );
            create index if not exists ix_studio_workspace_members_user on studio_workspace_members(lower(user_name),workspace_id);
            alter table studio_workspace_members add column if not exists send_as_self boolean not null default false;

            create table if not exists studio_job_members
            (
                job_id uuid not null references studio_jobs(id) on delete cascade,user_name text not null,display_name text not null,is_active boolean not null default true,
                inherit_workspace_permissions boolean not null default true,access_job boolean not null default true,view_job boolean not null default true,view_conversation boolean not null default true,
                reply_requester boolean not null default false,view_attachments boolean not null default true,add_attachments boolean not null default false,edit_job boolean not null default false,
                change_status boolean not null default false,assign_staff boolean not null default false,manage_schedule boolean not null default false,manage_production boolean not null default false,
                view_financials boolean not null default false,record_financial_activity boolean not null default false,use_pos boolean not null default false,view_pos_transactions boolean not null default false,
                refund_void_pos boolean not null default false,notify_job_members boolean not null default false,manage_job_members boolean not null default false,admin_job boolean not null default false,
                created_at_utc timestamptz not null,updated_at_utc timestamptz not null,primary key(job_id,user_name)
            );
            create index if not exists ix_studio_job_members_user on studio_job_members(lower(user_name),job_id);

            create table if not exists studio_schedule_resources
            (
                id uuid primary key,name text not null,resource_type text not null,is_active boolean not null default true,notes text null,created_at_utc timestamptz not null
            );
            create unique index if not exists ux_studio_schedule_resources_name on studio_schedule_resources(lower(name),lower(resource_type));

            create table if not exists studio_schedule_entries
            (
                id uuid primary key,job_id uuid null references studio_jobs(id) on delete cascade,booking_id uuid null references studio_bookings(id) on delete set null,
                title text not null,starts_at_utc timestamptz not null,ends_at_utc timestamptz not null,location text null,status text not null,notes text null,created_at_utc timestamptz not null,
                check(ends_at_utc > starts_at_utc)
            );
            create index if not exists ix_studio_schedule_entries_range on studio_schedule_entries(starts_at_utc,ends_at_utc);
            create index if not exists ix_studio_schedule_entries_job on studio_schedule_entries(job_id);

            create table if not exists studio_schedule_entry_resources
            (
                entry_id uuid not null references studio_schedule_entries(id) on delete cascade,resource_id uuid not null references studio_schedule_resources(id),primary key(entry_id,resource_id)
            );
            create index if not exists ix_studio_schedule_entry_resources_resource on studio_schedule_entry_resources(resource_id,entry_id);

            create table if not exists studio_job_activities
            (
                id uuid primary key,job_id uuid not null references studio_jobs(id) on delete cascade,activity_type text not null,title text not null,description text null,status text not null,
                priority text not null default 'Normal',assigned_to text null,scheduled_at_utc timestamptz null,due_at_utc timestamptz null,started_at_utc timestamptz null,completed_at_utc timestamptz null,
                location text null,blocked_reason text null,handoff_to text null,created_by text not null,created_at_utc timestamptz not null,updated_at_utc timestamptz not null
            );
            create index if not exists ix_studio_job_activities_job on studio_job_activities(job_id,status,due_at_utc);
            create index if not exists ix_studio_job_activities_assigned on studio_job_activities(lower(assigned_to),status,due_at_utc);

            do $$
            begin
                if to_regclass('public.studio_production_tasks') is not null then
                    insert into studio_job_activities(id,job_id,activity_type,title,description,status,priority,assigned_to,due_at_utc,started_at_utc,completed_at_utc,created_by,created_at_utc,updated_at_utc)
                    select id,job_id,'Other',title,description,
                           case status when 'NotStarted' then 'NotStarted' when 'InProgress' then 'InProgress' when 'Blocked' then 'Blocked' when 'Completed' then 'Completed' else 'NotStarted' end,
                           'Normal',assigned_to,due_at_utc,null,completed_at_utc,'production-task-migration',created_at_utc,coalesce(completed_at_utc,created_at_utc)
                    from studio_production_tasks
                    on conflict(id) do nothing;
                    drop table studio_production_tasks;
                end if;
            end $$;

            create table if not exists studio_photographers
            (
                user_name text primary key,display_name text not null,employment_type text not null,is_active boolean not null default true,skills text[] not null default '{}',
                default_equipment_notes text null,schedule_resource_id uuid not null references studio_schedule_resources(id),created_at_utc timestamptz not null,updated_at_utc timestamptz not null
            );
            create index if not exists ix_studio_photographers_active on studio_photographers(is_active,display_name);

            create table if not exists studio_photographer_availability
            (
                id uuid primary key,user_name text not null references studio_photographers(user_name) on delete cascade,starts_at_utc timestamptz not null,ends_at_utc timestamptz not null,
                availability_type text not null,note text null,created_at_utc timestamptz not null,check(ends_at_utc > starts_at_utc)
            );
            create index if not exists ix_studio_photographer_availability_range on studio_photographer_availability(lower(user_name),starts_at_utc,ends_at_utc);

            create table if not exists studio_shoot_sessions
            (
                id uuid primary key,job_id uuid not null references studio_jobs(id) on delete cascade,schedule_entry_id uuid null references studio_schedule_entries(id) on delete cascade,
                photographer_user_name text not null,assistant_user_name text null,call_time_utc timestamptz not null,planned_end_utc timestamptz not null,location text null,status text not null,
                checked_in_at_utc timestamptz null,checked_out_at_utc timestamptz null,image_count integer not null default 0,backup_confirmed boolean not null default false,reshoot_required boolean not null default false,
                coverage_notes text null,equipment_notes text null,handoff_status text not null default 'NotReady',handoff_to text null,created_at_utc timestamptz not null,updated_at_utc timestamptz not null,
                check(planned_end_utc > call_time_utc)
            );
            delete from studio_shoot_sessions where schedule_entry_id is null;
            alter table studio_shoot_sessions alter column schedule_entry_id set not null;
            create index if not exists ix_studio_shoot_sessions_job on studio_shoot_sessions(job_id,call_time_utc);
            create index if not exists ix_studio_shoot_sessions_photographer on studio_shoot_sessions(lower(photographer_user_name),call_time_utc);

            create or replace function studio_sync_shoot_from_schedule() returns trigger as $$
            begin
                update studio_shoot_sessions
                set call_time_utc=new.starts_at_utc,planned_end_utc=new.ends_at_utc,location=new.location,
                    status=case when new.status='Cancelled' then 'Cancelled' else status end,updated_at_utc=now()
                where schedule_entry_id=new.id;
                return new;
            end;
            $$ language plpgsql;
            drop trigger if exists trg_studio_sync_shoot_from_schedule on studio_schedule_entries;
            create trigger trg_studio_sync_shoot_from_schedule after update of starts_at_utc,ends_at_utc,location,status on studio_schedule_entries
            for each row execute function studio_sync_shoot_from_schedule();

            create table if not exists studio_media_archives
            (
                id uuid primary key,job_id uuid not null references studio_jobs(id) on delete cascade,shoot_id uuid null references studio_shoot_sessions(id) on delete set null,
                relative_path text not null,backup_relative_path text null,file_count integer not null default 0,total_bytes bigint not null default 0,manifest_sha256 text null,
                primary_verified_at_utc timestamptz null,backup_verified_at_utc timestamptz null,created_at_utc timestamptz not null,updated_at_utc timestamptz not null
            );
            create index if not exists ix_studio_media_archives_job on studio_media_archives(job_id,created_at_utc desc);
            create index if not exists ix_studio_media_archives_shoot on studio_media_archives(shoot_id);

            create table if not exists studio_operational_audit
            (
                id uuid primary key,job_id uuid not null references studio_jobs(id) on delete cascade,activity_id uuid null references studio_job_activities(id) on delete set null,
                action text not null,user_name text not null,detail text null,created_at_utc timestamptz not null
            );
            create index if not exists ix_studio_operational_audit_job on studio_operational_audit(job_id,created_at_utc desc);

            create table if not exists studio_booking_api_nonces(client_id text not null,nonce text not null,used_at_utc timestamptz not null,primary key(client_id,nonce));
            create index if not exists ix_studio_booking_api_nonces_used on studio_booking_api_nonces(used_at_utc);

            alter table studio_attachments add column if not exists sha256 text null;
            alter table studio_attachments add column if not exists uploaded_by text null;
            alter table studio_attachments add column if not exists storage_status text not null default 'Stored';
            alter table studio_attachments add column if not exists source_key text null;
            create unique index if not exists ux_studio_attachments_stored_name on studio_attachments(stored_name);
            create unique index if not exists ux_studio_attachments_source_key on studio_attachments(source_key) where source_key is not null;
            alter table studio_user_profiles add column if not exists theme_name text not null default 'studio-default';
            alter table studio_outbound_email add column if not exists sent_by_user_name text null;
            alter table studio_outbound_email add column if not exists sending_identity_type text not null default 'Workspace';
            alter table studio_outbound_email add column if not exists attempts integer not null default 0;
            alter table studio_outbound_email add column if not exists last_error text null;
            alter table studio_outbound_email add column if not exists next_attempt_at_utc timestamptz null;
            alter table studio_outbound_email add column if not exists provider_accepted_at_utc timestamptz null;
            """;

        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
