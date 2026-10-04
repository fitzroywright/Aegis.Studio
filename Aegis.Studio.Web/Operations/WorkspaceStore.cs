using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Operations;

public sealed class WorkspaceStore
{
    private readonly StudioDatabase database;

    public WorkspaceStore(StudioDatabase database)
    {
        this.database = database;
    }

    public IReadOnlyList<StudioWorkspaceDefinition> GetAll(bool includeDisabled = false)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select id, name, email_address, route_key, is_enabled, sort_order
            from studio_workspaces
            where $1 or is_enabled
            order by sort_order, name
            """;
        command.Parameters.AddWithValue(includeDisabled);
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<StudioWorkspaceDefinition> values = [];
        while (reader.Read())
        {
            values.Add(Read(reader));
        }
        return values;
    }

    public StudioWorkspaceDefinition? Get(Guid id)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id, name, email_address, route_key, is_enabled, sort_order from studio_workspaces where id = $1";
        command.Parameters.AddWithValue(id);
        using NpgsqlDataReader reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public StudioWorkspaceDefinition? FindByAddress(string address)
    {
        string normalized = Normalize(address);
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id, name, email_address, route_key, is_enabled, sort_order from studio_workspaces where is_enabled and lower(email_address) = $1";
        command.Parameters.AddWithValue(normalized);
        using NpgsqlDataReader reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public StudioWorkspaceDefinition? FindByRouteKey(string routeKey)
    {
        string normalized = Normalize(routeKey);
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id, name, email_address, route_key, is_enabled, sort_order from studio_workspaces where is_enabled and lower(route_key) = $1";
        command.Parameters.AddWithValue(normalized);
        using NpgsqlDataReader reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public StudioWorkspaceDefinition Create(SaveWorkspaceRequest request)
    {
        Guid id = Guid.NewGuid();
        StudioWorkspaceDefinition workspace = new(id, request.Name.Trim(), Clean(request.EmailAddress), Clean(request.RouteKey) ?? GenerateRouteKey(id), request.IsEnabled, request.SortOrder);
        Save(workspace);
        return workspace;
    }

    public StudioWorkspaceDefinition Update(Guid id, SaveWorkspaceRequest request)
    {
        StudioWorkspaceDefinition? existing = Get(id);
        if (existing is null)
        {
            throw new KeyNotFoundException("Workspace was not found.");
        }
        StudioWorkspaceDefinition workspace = new(id, request.Name.Trim(), Clean(request.EmailAddress), Clean(request.RouteKey) ?? existing.RouteKey ?? GenerateRouteKey(id), request.IsEnabled, request.SortOrder);
        Save(workspace);
        return workspace;
    }

    private void Save(StudioWorkspaceDefinition workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace.Name))
        {
            throw new InvalidOperationException("Workspace name is required.");
        }

        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            insert into studio_workspaces(id, name, email_address, route_key, is_enabled, sort_order, created_at_utc, updated_at_utc)
            values ($1, $2, $3, $4, $5, $6, now(), now())
            on conflict(id) do update set
                name = excluded.name,
                email_address = excluded.email_address,
                route_key = excluded.route_key,
                is_enabled = excluded.is_enabled,
                sort_order = excluded.sort_order,
                updated_at_utc = now()
            """;
        command.Parameters.AddWithValue(workspace.Id);
        command.Parameters.AddWithValue(workspace.Name);
        command.Parameters.AddWithValue((object?)workspace.EmailAddress ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)workspace.RouteKey ?? DBNull.Value);
        command.Parameters.AddWithValue(workspace.IsEnabled);
        command.Parameters.AddWithValue(workspace.SortOrder);
        command.ExecuteNonQuery();
    }

    private static StudioWorkspaceDefinition Read(NpgsqlDataReader reader)
        => new(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetBoolean(4), reader.GetInt32(5));

    private static string GenerateRouteKey(Guid id) => $"workspace-{id:N}";

    private static string Normalize(string value) => value.Trim().Trim('<', '>').ToLowerInvariant();
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record StudioWorkspaceDefinition(Guid Id, string Name, string? EmailAddress, string? RouteKey, bool IsEnabled, int SortOrder);
public sealed record SaveWorkspaceRequest(string Name, string? EmailAddress, string? RouteKey, bool IsEnabled, int SortOrder);

