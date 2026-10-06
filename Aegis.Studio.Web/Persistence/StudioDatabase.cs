using Npgsql;

namespace Aegis.Studio.Web.Persistence;

public sealed class StudioDatabase
{
    private readonly NpgsqlDataSource dataSource;
    private readonly string? connectionString;

    public StudioDatabase(NpgsqlDataSource dataSource)
    {
        this.dataSource = dataSource;
    }

    public StudioDatabase(NpgsqlDataSource dataSource, string connectionString)
    {
        this.dataSource = dataSource;
        this.connectionString = connectionString;
    }

    public string ConnectionString => connectionString
        ?? throw new InvalidOperationException("The resolved Studio connection string is not available.");

    public NpgsqlConnection OpenConnection() => dataSource.OpenConnection();

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
            create unique index if not exists ux_studio_workspaces_email on studio_workspaces(lower(email_address)) where email_address is not null;
            create unique index if not exists ux_studio_workspaces_route_key on studio_workspaces(lower(route_key)) where route_key is not null;

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
            create unique index if not exists ux_studio_messages_external_id on studio_messages(external_message_id) where external_message_id is not null;
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
            alter table studio_financial_events add column if not exists payment_method text null;
            alter table studio_financial_events add column if not exists recorded_by text null;
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
            create unique index if not exists ux_studio_bookings_external_id on studio_bookings(external_booking_id) where external_booking_id is not null;
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
                whatsapp_enabled boolean not null default false,
                whatsapp_mobile text null,
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

            create table if not exists studio_attachments
            (
                id uuid primary key,
                conversation_id uuid not null references studio_conversations(id) on delete cascade,
                file_name text not null,
                stored_name text not null,
                content_type text not null,
                size_bytes bigint not null,
                visibility text not null,
                created_at_utc timestamptz not null
            );
            create index if not exists ix_studio_attachments_conversation on studio_attachments(conversation_id, created_at_utc desc);

            create table if not exists studio_outbound_email
            (
                id uuid primary key,
                conversation_id uuid not null references studio_conversations(id) on delete cascade,
                from_address text not null,
                to_address text not null,
                subject text not null,
                body text not null,
                thread_id text null,
                status text not null,
                created_at_utc timestamptz not null,
                provider_message_id text null,
                kind text not null
            );
            create index if not exists ix_studio_outbound_email_status on studio_outbound_email(status, created_at_utc);

            create table if not exists studio_unrouted_email
            (
                id uuid primary key,
                provider_message_id text not null unique,
                thread_id text null,
                from_name text not null,
                from_address text not null,
                to_addresses text[] not null,
                cc_addresses text[] not null,
                internet_headers jsonb not null,
                subject text not null,
                body text not null,
                received_at_utc timestamptz not null,
                failure_reason text not null,
                resolved_at_utc timestamptz null,
                resolved_workspace_id uuid null references studio_workspaces(id),
                resolved_workspace_name text null
            );

            create table if not exists studio_pos_catalog
            (
                id uuid primary key,
                code text not null,
                name text not null,
                category text not null,
                unit_price numeric(18,2) not null,
                is_active boolean not null,
                sort_order integer not null,
                updated_at_utc timestamptz not null
            );

            create table if not exists studio_pos_sales
            (
                id uuid primary key,
                reference_type text not null,
                reference_id text not null,
                created_at_utc timestamptz not null,
                submitted_at_utc timestamptz null,
                completed_at_utc timestamptz null,
                voided_at_utc timestamptz null,
                voided_by text null,
                void_reason text null,
                cashier_user_id text null,
                terminal_id text null,
                status integer not null,
                financial_event_posted boolean not null,
                client_request_id uuid null
            );
            alter table studio_pos_sales add column if not exists applies_to_balance boolean not null default false;
            create unique index if not exists ux_studio_pos_sales_client_request on studio_pos_sales(reference_type, reference_id, client_request_id) where client_request_id is not null;
            create index if not exists ix_studio_pos_sales_reference on studio_pos_sales(reference_type, reference_id, created_at_utc desc);

            create table if not exists studio_pos_sale_lines
            (
                id uuid primary key,
                sale_id uuid not null references studio_pos_sales(id) on delete cascade,
                code text not null,
                description text not null,
                unit_price numeric(18,2) not null,
                quantity integer not null
            );
            create table if not exists studio_pos_tenders
            (
                id uuid primary key,
                sale_id uuid not null references studio_pos_sales(id) on delete cascade,
                kind integer not null,
                amount numeric(18,2) not null,
                reference text null
            );
            create table if not exists studio_pos_refunds
            (
                id uuid primary key,
                sale_id uuid not null references studio_pos_sales(id) on delete cascade,
                reason text not null,
                created_at_utc timestamptz not null
            );
            create table if not exists studio_pos_refund_lines
            (
                id uuid primary key,
                refund_id uuid not null references studio_pos_refunds(id) on delete cascade,
                sale_line_id uuid not null,
                unit_price numeric(18,2) not null,
                quantity integer not null
            );
            create table if not exists studio_pos_refund_tenders
            (
                id uuid primary key,
                refund_id uuid not null references studio_pos_refunds(id) on delete cascade,
                kind integer not null,
                amount numeric(18,2) not null,
                reference text null
            );
            create table if not exists studio_pos_audit
            (
                id uuid primary key,
                action text not null,
                sale_id uuid not null,
                job_id uuid not null,
                user_id text not null,
                amount numeric(18,2) not null,
                detail text null,
                created_at_utc timestamptz not null
            );
            create index if not exists ix_studio_pos_audit_created on studio_pos_audit(created_at_utc desc);

            create table if not exists studio_pos_receipts
            (
                id uuid primary key,
                sale_id uuid not null unique,
                job_id uuid not null,
                receipt_number text not null unique,
                client_name text not null,
                job_title text not null,
                cashier_user_id text not null,
                currency text not null,
                total numeric(18,2) not null,
                created_at_utc timestamptz not null
            );
            create table if not exists studio_pos_receipt_lines
            (
                id uuid primary key,
                receipt_id uuid not null references studio_pos_receipts(id) on delete cascade,
                description text not null,
                quantity integer not null,
                unit_price numeric(18,2) not null,
                total numeric(18,2) not null,
                sort_order integer not null
            );
            create table if not exists studio_pos_receipt_tenders
            (
                id uuid primary key,
                receipt_id uuid not null references studio_pos_receipts(id) on delete cascade,
                kind text not null,
                amount numeric(18,2) not null,
                reference text null,
                sort_order integer not null
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

