using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Aegis.Studio.Web.Persistence;

public sealed class PostgresDocumentStore
{
    private readonly NpgsqlDataSource dataSource;
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    public PostgresDocumentStore(NpgsqlDataSource dataSource)
    {
        this.dataSource = dataSource;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            create table if not exists studio_documents
            (
                collection text not null,
                id uuid not null,
                payload jsonb not null,
                updated_at_utc timestamptz not null,
                primary key (collection, id)
            );
            create index if not exists ix_studio_documents_collection on studio_documents(collection);
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

    public IReadOnlyList<T> GetAll<T>(string collection)
    {
        using NpgsqlCommand command = dataSource.CreateCommand("select payload::text from studio_documents where collection = $1 order by updated_at_utc");
        command.Parameters.AddWithValue(collection);
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<T> items = [];
        while (reader.Read())
        {
            T? item = JsonSerializer.Deserialize<T>(reader.GetString(0), jsonOptions);
            if (item is not null)
            {
                items.Add(item);
            }
        }
        return items;
    }

    public T? Get<T>(string collection, Guid id)
    {
        using NpgsqlCommand command = dataSource.CreateCommand("select payload::text from studio_documents where collection = $1 and id = $2");
        command.Parameters.AddWithValue(collection);
        command.Parameters.AddWithValue(id);
        object? value = command.ExecuteScalar();
        return value is string json ? JsonSerializer.Deserialize<T>(json, jsonOptions) : default;
    }

    public void Upsert<T>(string collection, Guid id, T value)
    {
        string json = JsonSerializer.Serialize(value, jsonOptions);
        using NpgsqlCommand command = dataSource.CreateCommand("""
            insert into studio_documents(collection, id, payload, updated_at_utc)
            values ($1, $2, $3, $4)
            on conflict (collection, id) do update set payload = excluded.payload, updated_at_utc = excluded.updated_at_utc
            """);
        command.Parameters.AddWithValue(collection);
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, json);
        command.Parameters.AddWithValue(DateTimeOffset.UtcNow);
        command.ExecuteNonQuery();
    }

    public void Delete(string collection, Guid id)
    {
        using NpgsqlCommand command = dataSource.CreateCommand("delete from studio_documents where collection = $1 and id = $2");
        command.Parameters.AddWithValue(collection);
        command.Parameters.AddWithValue(id);
        command.ExecuteNonQuery();
    }

    public string? GetState(string key)
    {
        using NpgsqlCommand command = dataSource.CreateCommand("select value from studio_state where key = $1");
        command.Parameters.AddWithValue(key);
        return command.ExecuteScalar() as string;
    }

    public void SetState(string key, string? value)
    {
        using NpgsqlCommand command = dataSource.CreateCommand("""
            insert into studio_state(key, value, updated_at_utc)
            values ($1, $2, $3)
            on conflict (key) do update set value = excluded.value, updated_at_utc = excluded.updated_at_utc
            """);
        command.Parameters.AddWithValue(key);
        command.Parameters.AddWithValue((object?)value ?? DBNull.Value);
        command.Parameters.AddWithValue(DateTimeOffset.UtcNow);
        command.ExecuteNonQuery();
    }

    public void DeleteState(string key)
    {
        using NpgsqlCommand command = dataSource.CreateCommand("delete from studio_state where key = $1");
        command.Parameters.AddWithValue(key);
        command.ExecuteNonQuery();
    }
}
