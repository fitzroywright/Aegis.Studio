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
                scope_type text not null check(scope_type in ('Direct','Workspace','Job')),
                scope_id uuid null,
                direct_key text null,
                title text not null,
                created_by text not null,
                created_at_utc timestamptz not null
            );
            create unique index if not exists ux_studio_message_threads_direct on studio_message_threads(direct_key) where direct_key is not null;
            create unique index if not exists ux_studio_message_threads_scope on studio_message_threads(scope_type,scope_id) where scope_id is not null;

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
