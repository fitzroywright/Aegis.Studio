using Microsoft.AspNetCore.Authorization;
using Npgsql;
using Aegis.Studio.Web.Persistence;

namespace Aegis.Studio.Web.Operations;

public static class WorkflowOperationsEndpoints
{
    public static void MapStudioWorkflowOperations(this WebApplication app)
    {
        WorkflowOperationsStore store = new(app.Services.GetRequiredService<StudioDatabase>());
        RouteGroupBuilder group = app.MapGroup("/api/workflow-operations").RequireAuthorization(new AuthorizeAttribute { Roles = "Administrator" });

        group.MapGet("/summary", () => Results.Ok(store.GetSummary()));
        group.MapGet("/failed", (int? count) => Results.Ok(store.GetRunsByStatus("Failed", Math.Clamp(count ?? 100, 1, 1000))));
        group.MapGet("/waiting", (int? count) => Results.Ok(store.GetRunsByStatus("Waiting", Math.Clamp(count ?? 100, 1, 1000))));
        group.MapGet("/runs/{runId:guid}/log", (Guid runId) => Results.Ok(store.GetLog(runId)));
        group.MapPost("/runs/{runId:guid}/retry", (Guid runId) =>
        {
            try { return Results.Ok(store.Retry(runId)); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });
        group.MapPost("/runs/{runId:guid}/cancel", (Guid runId) =>
        {
            try { return Results.Ok(store.Cancel(runId)); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });
    }
}

public sealed class WorkflowOperationsStore
{
    private readonly StudioDatabase database;

    public WorkflowOperationsStore(StudioDatabase database) => this.database = database;

    public WorkflowOperationsSummary GetSummary()
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select
                count(*) filter(where status='Running'),
                count(*) filter(where status='Waiting'),
                count(*) filter(where status='Failed'),
                count(*) filter(where status='Completed'),
                count(*) filter(where status='Cancelled'),
                min(next_action_at_utc) filter(where status='Waiting')
            from studio_workflow_runs
            """;
        using NpgsqlDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return new WorkflowOperationsSummary(0,0,0,0,0,null);
        return new WorkflowOperationsSummary(
            reader.GetInt64(0),reader.GetInt64(1),reader.GetInt64(2),reader.GetInt64(3),reader.GetInt64(4),
            reader.IsDBNull(5)?null:reader.GetFieldValue<DateTimeOffset>(5));
    }

    public IReadOnlyList<WorkflowOperationalRun> GetRunsByStatus(string status, int count)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select r.id,r.workflow_id,d.name,r.job_id,r.trigger_event,r.status,r.action_index,r.next_action_at_utc,r.last_error,r.created_at_utc,r.updated_at_utc,r.completed_at_utc
            from studio_workflow_runs r
            join studio_workflow_definitions d on d.id=r.workflow_id
            where r.status=$1
            order by r.updated_at_utc desc
            limit $2
            """;
        command.Parameters.AddWithValue(status);
        command.Parameters.AddWithValue(count);
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<WorkflowOperationalRun> values = [];
        while (reader.Read()) values.Add(ReadRun(reader));
        return values;
    }

    public IReadOnlyList<WorkflowOperationalLog> GetLog(Guid runId)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id,run_id,action_index,action_type,message,created_at_utc from studio_workflow_run_log where run_id=$1 order by created_at_utc,action_index";
        command.Parameters.AddWithValue(runId);
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<WorkflowOperationalLog> values = [];
        while (reader.Read()) values.Add(new WorkflowOperationalLog(reader.GetGuid(0),reader.GetGuid(1),reader.GetInt32(2),reader.GetString(3),reader.GetString(4),reader.GetFieldValue<DateTimeOffset>(5)));
        return values;
    }

    public WorkflowOperationalRun Retry(Guid runId)
    {
        WorkflowOperationalRun current = Get(runId) ?? throw new KeyNotFoundException("Workflow run was not found.");
        if (!current.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Only failed workflow runs can be retried.");
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlTransaction transaction = connection.BeginTransaction();
        using (NpgsqlCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "update studio_workflow_runs set status='Waiting',next_action_at_utc=now(),last_error=null,lease_owner=null,lease_until_utc=null,updated_at_utc=now(),completed_at_utc=null where id=$1 and status='Failed'";
            command.Parameters.AddWithValue(runId);
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Workflow run changed before retry could be scheduled.");
        }
        AppendAdministrativeLog(connection, transaction, runId, current.ActionIndex, "Retry", "Administrator scheduled the failed run for retry.");
        transaction.Commit();
        return Get(runId)!;
    }

    public WorkflowOperationalRun Cancel(Guid runId)
    {
        WorkflowOperationalRun current = Get(runId) ?? throw new KeyNotFoundException("Workflow run was not found.");
        if (current.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase) || current.Status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Completed or cancelled workflow runs cannot be cancelled again.");
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlTransaction transaction = connection.BeginTransaction();
        using (NpgsqlCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "update studio_workflow_runs set status='Cancelled',next_action_at_utc=null,lease_owner=null,lease_until_utc=null,updated_at_utc=now(),completed_at_utc=now() where id=$1 and status not in ('Completed','Cancelled')";
            command.Parameters.AddWithValue(runId);
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Workflow run changed before it could be cancelled.");
        }
        AppendAdministrativeLog(connection, transaction, runId, current.ActionIndex, "Cancel", "Administrator cancelled the workflow run.");
        transaction.Commit();
        return Get(runId)!;
    }

    private WorkflowOperationalRun? Get(Guid runId)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select r.id,r.workflow_id,d.name,r.job_id,r.trigger_event,r.status,r.action_index,r.next_action_at_utc,r.last_error,r.created_at_utc,r.updated_at_utc,r.completed_at_utc
            from studio_workflow_runs r join studio_workflow_definitions d on d.id=r.workflow_id where r.id=$1
            """;
        command.Parameters.AddWithValue(runId);
        using NpgsqlDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadRun(reader) : null;
    }

    private static WorkflowOperationalRun ReadRun(NpgsqlDataReader reader)
        => new(reader.GetGuid(0),reader.GetGuid(1),reader.GetString(2),reader.GetGuid(3),reader.GetString(4),reader.GetString(5),reader.GetInt32(6),reader.IsDBNull(7)?null:reader.GetFieldValue<DateTimeOffset>(7),reader.IsDBNull(8)?null:reader.GetString(8),reader.GetFieldValue<DateTimeOffset>(9),reader.GetFieldValue<DateTimeOffset>(10),reader.IsDBNull(11)?null:reader.GetFieldValue<DateTimeOffset>(11));

    private static void AppendAdministrativeLog(NpgsqlConnection connection,NpgsqlTransaction transaction,Guid runId,int actionIndex,string actionType,string message)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "insert into studio_workflow_run_log(id,run_id,action_index,action_type,message,created_at_utc) values($1,$2,$3,$4,$5,now())";
        command.Parameters.AddWithValue(Guid.NewGuid());command.Parameters.AddWithValue(runId);command.Parameters.AddWithValue(actionIndex);command.Parameters.AddWithValue(actionType);command.Parameters.AddWithValue(message);command.ExecuteNonQuery();
    }
}

public sealed record WorkflowOperationsSummary(long Running,long Waiting,long Failed,long Completed,long Cancelled,DateTimeOffset? NextDueAtUtc);
public sealed record WorkflowOperationalRun(Guid Id,Guid WorkflowId,string WorkflowName,Guid JobId,string TriggerEvent,string Status,int ActionIndex,DateTimeOffset? NextActionAtUtc,string? LastError,DateTimeOffset CreatedAtUtc,DateTimeOffset UpdatedAtUtc,DateTimeOffset? CompletedAtUtc);
public sealed record WorkflowOperationalLog(Guid Id,Guid RunId,int ActionIndex,string ActionType,string Message,DateTimeOffset CreatedAtUtc);
