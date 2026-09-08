using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Email;

public sealed class GraphEmailDeltaStateStore
{
    private const string DeltaKey = "graph-email-delta-link";
    private readonly StudioDatabase database;

    public GraphEmailDeltaStateStore(StudioDatabase database)
    {
        this.database = database;
    }

    public string? GetDeltaLink()
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select value from studio_state where key=$1";
        command.Parameters.AddWithValue(DeltaKey);
        return command.ExecuteScalar() as string;
    }

    public void SaveDeltaLink(string deltaLink)
    {
        if (string.IsNullOrWhiteSpace(deltaLink)) throw new ArgumentException("Delta link is required.", nameof(deltaLink));
        using NpgsqlConnection connection=database.OpenConnection();using NpgsqlCommand command=connection.CreateCommand();command.CommandText="insert into studio_state(key,value,updated_at_utc) values ($1,$2,$3) on conflict(key) do update set value=excluded.value,updated_at_utc=excluded.updated_at_utc";command.Parameters.AddWithValue(DeltaKey);command.Parameters.AddWithValue(deltaLink.Trim());command.Parameters.AddWithValue(DateTimeOffset.UtcNow);command.ExecuteNonQuery();
    }

    public void Clear()
    {
        using NpgsqlConnection connection=database.OpenConnection();using NpgsqlCommand command=connection.CreateCommand();command.CommandText="delete from studio_state where key=$1";command.Parameters.AddWithValue(DeltaKey);command.ExecuteNonQuery();
    }
}

public sealed record GraphEmailDeltaState(string DeltaLink,DateTimeOffset UpdatedAt);
