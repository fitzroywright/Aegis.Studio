using Npgsql;

namespace Aegis.Studio.Web.Messaging;

public sealed class StudioMessagingDatabase
{
    private readonly NpgsqlDataSource dataSource;

    public StudioMessagingDatabase(NpgsqlDataSource dataSource)
    {
        this.dataSource = dataSource;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            create table if not exists studio_message_threads
            (
                id uuid primary key,
                scope_type text not null check(scope_type in ('Direct','Workspace','Job','Person')),
                scope_id uuid null,
                direct_key text null,
                title text not null,
                created_by text not null,
                created_at_utc timestamptz not null
            );
            create unique index if not exists ux_studio_message_threads_direct on studio_message_threads(direct_key) where direct_key is not null;
            create unique index if not exists ux_studio_message_threads_scope on studio_message_threads(scope_type,scope_id) where scope_id is not null;

            alter table studio_message_threads drop constraint if exists studio_message_threads_scope_type_check;
            alter table studio_message_threads add constraint studio_message_threads_scope_type_check check(scope_type in ('Direct','Workspace','Job','Person'));
            alter table studio_message_threads add column if not exists external_conversation_id uuid null references studio_conversations(id) on delete cascade;
            alter table studio_message_threads add column if not exists owner_user_name text null;
            alter table studio_message_threads add column if not exists external_address text null;
            create unique index if not exists ux_studio_message_threads_external_conversation on studio_message_threads(external_conversation_id) where external_conversation_id is not null;
            create index if not exists ix_studio_message_threads_owner on studio_message_threads(lower(owner_user_name)) where owner_user_name is not null;

            create table if not exists studio_message_addresses
            (
                address text primary key,
                destination_type text not null check(destination_type in ('Person','Workspace','Job')),
                person_user_name text null,
                workspace_id uuid null references studio_workspaces(id),
                job_id uuid null references studio_jobs(id),
                display_name text not null,
                is_active boolean not null default true,
                created_at_utc timestamptz not null,
                updated_at_utc timestamptz not null
            );
            create unique index if not exists ux_studio_message_addresses_person
                on studio_message_addresses(lower(person_user_name))
                where destination_type='Person' and person_user_name is not null;

            create table if not exists studio_message_thread_members
            (
                thread_id uuid not null references studio_message_threads(id) on delete cascade,
                user_name text not null,
                joined_at_utc timestamptz not null,
                primary key(thread_id,user_name)
            );
            create index if not exists ix_studio_message_thread_members_user on studio_message_thread_members(lower(user_name),thread_id);

            create table if not exists studio_inapp_messages
            (
                id uuid primary key,
                thread_id uuid not null references studio_message_threads(id) on delete cascade,
                sender_user_name text not null,
                body text not null,
                created_at_utc timestamptz not null
            );
            alter table studio_inapp_messages add column if not exists sender_display_name text null;
            alter table studio_inapp_messages add column if not exists sender_address text null;
            alter table studio_inapp_messages add column if not exists external_message_id text null;
            create unique index if not exists ux_studio_inapp_messages_external_message on studio_inapp_messages(external_message_id) where external_message_id is not null;
            create index if not exists ix_studio_inapp_messages_thread on studio_inapp_messages(thread_id,created_at_utc);

            create table if not exists studio_message_reads
            (
                thread_id uuid not null references studio_message_threads(id) on delete cascade,
                user_name text not null,
                last_read_at_utc timestamptz not null,
                primary key(thread_id,user_name)
            );
            """;

        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
