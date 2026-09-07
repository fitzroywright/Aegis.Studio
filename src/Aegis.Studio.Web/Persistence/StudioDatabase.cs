using Npgsql;

namespace Aegis.Studio.Web.Persistence;

public sealed class StudioDatabase
{
    private readonly NpgsqlDataSource dataSource;

    public StudioDatabase(NpgsqlDataSource dataSource)
    {
        this.dataSource = dataSource;
    }

    public NpgsqlConnection OpenConnection()
    {
        NpgsqlConnection connection = dataSource.OpenConnection();
        return connection;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            create table if not exists studio_workspaces
            (
                id uuid primary key,
                name text not null,
                email_address text null,
                route_key text null,
                is_enabled boolean not null default true,
                sort_order integer not null default 0,
                created_at_utc timestamptz not null,
                updated_at_utc timestamptz not null
            );
            create unique index if not exists ux_studio_workspaces_email
                on studio_workspaces(lower(email_address)) where email_address is not null;
            create unique index if not exists ux_studio_workspaces_route_key
                on studio_workspaces(lower(route_key)) where route_key is not null;

            create table if not exists studio_conversations
            (
                id uuid primary key,
                workspace_id uuid not null references studio_workspaces(id),
                subject text not null,
                contact_name text not null,
                contact_address text not null,
                channel text not null,
                is_closed boolean not null default false,
                job_id uuid null,
                external_thread_id text null,
                created_at_utc timestamptz not null
            );
            create index if not exists ix_studio_conversations_workspace on studio_conversations(workspace_id, is_closed);
            create index if not exists ix_studio_conversations_thread on studio_conversations(external_thread_id);

            create table if not exists studio_messages
            (
                id uuid primary key,
                conversation_id uuid not null references studio_conversations(id) on delete cascade,
                author_user_name text null,
                author_display_name text not null,
                visibility text not null,
                body text not null,
                created_at_utc timestamptz not null,
                external_message_id text null
            );
            create unique index if not exists ux_studio_messages_external_id
                on studio_messages(external_message_id) where external_message_id is not null;
            create index if not exists ix_studio_messages_conversation on studio_messages(conversation_id, created_at_utc);

            create table if not exists studio_jobs
            (
                id uuid primary key,
                workspace_id uuid not null references studio_workspaces(id),
                conversation_id uuid not null references studio_conversations(id),
                title text not null,
                client_name text not null,
                status text not null,
                event_date date null,
                created_at_utc timestamptz not null,
                assigned_to text null
            );
            create index if not exists ix_studio_jobs_workspace on studio_jobs(workspace_id, created_at_utc desc);
            alter table studio_conversations drop constraint if exists fk_studio_conversations_job;
            alter table studio_conversations add constraint fk_studio_conversations_job foreign key(job_id) references studio_jobs(id) on delete set null;

            create table if not exists studio_financial_events
            (
                id uuid primary key,
                job_id uuid not null references studio_jobs(id) on delete cascade,
                kind text not null,
                amount numeric(18,2) not null,
                note text null,
                created_at_utc timestamptz not null
            );
            create index if not exists ix_studio_financial_events_job on studio_financial_events(job_id, created_at_utc);

            create table if not exists studio_bookings
            (
                id uuid primary key,
                job_id uuid not null references studio_jobs(id),
                workspace_id uuid not null references studio_workspaces(id),
                client_name text not null,
                client_email text not null,
                client_phone text null,
                session_type text not null,
                starts_at_utc timestamptz not null,
                ends_at_utc timestamptz not null,
                location text null,
                notes text null,
                source integer not null,
                status integer not null,
                external_booking_id text null,
                created_at_utc timestamptz not null
            );
            create unique index if not exists ux_studio_bookings_external_id
                on studio_bookings(external_booking_id) where external_booking_id is not null;
            create index if not exists ix_studio_bookings_start on studio_bookings(starts_at_utc);

            create table if not exists studio_booking_payments
            (
                id uuid primary key,
                booking_id uuid not null references studio_bookings(id) on delete cascade,
                amount numeric(18,2) not null,
                currency text not null,
                reference text not null,
                provider text not null,
                source text not null,
                confirmed_at_utc timestamptz not null,
                received_at_utc timestamptz not null
            );
            create unique index if not exists ux_studio_booking_payments_reference on studio_booking_payments(lower(reference));

            create table if not exists studio_user_profiles
            (
                id uuid primary key,
                user_name text not null unique,
                display_name text not null,
                email text null,
                department text null,
                title text null,
                email_enabled boolean not null,
                in_app_enabled boolean not null,
                slack_enabled boolean not null,
                teams_enabled boolean not null,
                new_workspace_message boolean not null,
                job_assigned boolean not null,
                booking_changed boolean not null,
                payment_changed boolean not null,
                job_status_changed boolean not null,
                mentions_and_replies boolean not null,
                delivery_mode text not null,
                quiet_hours_start time null,
                quiet_hours_end time null,
                created_at_utc timestamptz not null,
                updated_at_utc timestamptz not null
            );

            create table if not exists studio_state
            (
                key text primary key,
                value text null,
                updated_at_utc timestamptz not null
            );
            """;

        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
