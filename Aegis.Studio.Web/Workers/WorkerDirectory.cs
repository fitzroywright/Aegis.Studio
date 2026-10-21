using Aegis.Studio.Domain.Workers;
using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Workers;

public sealed class WorkerDirectory(StudioDatabase database)
{
    public IReadOnlyList<WorkerType> GetTypes(bool includeInactive = false)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select code,display_name,can_be_primary from studio_worker_types where $1 or is_active order by display_name";
        command.Parameters.AddWithValue(includeInactive);
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<WorkerType> result = [];
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));
        return result;
    }

    public void SaveWorkerType(WorkerType type)
    {
        if (string.IsNullOrWhiteSpace(type.Code) || string.IsNullOrWhiteSpace(type.DisplayName))
            throw new ArgumentException("Worker type code and display name are required.");
        string code = type.NormalizedCode;
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "insert into studio_worker_types(code,display_name,can_be_primary,is_active,updated_at_utc) values($1,$2,$3,true,now()) on conflict(code) do update set display_name=excluded.display_name,can_be_primary=excluded.can_be_primary,is_active=true,updated_at_utc=now()";
        command.Parameters.AddWithValue(code);
        command.Parameters.AddWithValue(type.DisplayName.Trim());
        command.Parameters.AddWithValue(type.CanBePrimary);
        command.ExecuteNonQuery();
    }

    public void SetWorkerTypeActive(string code, bool active)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Worker type code is required.");
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "update studio_worker_types set is_active=$2,updated_at_utc=now() where code=$1";
        command.Parameters.AddWithValue(code.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue(active);
        if (command.ExecuteNonQuery() == 0) throw new KeyNotFoundException("Worker type was not found.");
    }

    public IReadOnlyList<Worker> GetWorkers(string? workerTypeCode = null, bool includeInactive = false)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select user_name,display_name,worker_type_code,is_active,email,phone
            from studio_workers
            where ($1::text is null or worker_type_code=$1) and ($2 or is_active)
            order by display_name,user_name
            """;
        command.Parameters.AddWithValue((object?)workerTypeCode?.Trim().ToUpperInvariant() ?? DBNull.Value);
        command.Parameters.AddWithValue(includeInactive);
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<Worker> result = [];
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        return result;
    }

    public void SaveWorker(Worker worker)
    {
        if (string.IsNullOrWhiteSpace(worker.UserName) || string.IsNullOrWhiteSpace(worker.WorkerTypeCode))
            throw new ArgumentException("Worker name and worker type are required.");
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            insert into studio_workers(user_name,display_name,worker_type_code,is_active,email,phone,updated_at_utc)
            values($1,$2,$3,$4,$5,$6,now())
            on conflict(user_name) do update set display_name=excluded.display_name,worker_type_code=excluded.worker_type_code,
                is_active=excluded.is_active,email=excluded.email,phone=excluded.phone,updated_at_utc=now()
            """;
        command.Parameters.AddWithValue(worker.UserName.Trim());
        command.Parameters.AddWithValue(string.IsNullOrWhiteSpace(worker.DisplayName) ? worker.UserName.Trim() : worker.DisplayName.Trim());
        command.Parameters.AddWithValue(worker.NormalizedWorkerTypeCode);
        command.Parameters.AddWithValue(worker.IsActive);
        command.Parameters.AddWithValue((object?)worker.Email ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)worker.Phone ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public WorkerAssignment Assign(WorkerAssignment assignment)
    {
        if (assignment.Id == Guid.Empty || assignment.JobId == Guid.Empty)
            throw new ArgumentException("A valid assignment and job are required.");
        if (string.IsNullOrWhiteSpace(assignment.WorkerUserName) || string.IsNullOrWhiteSpace(assignment.WorkerTypeCode))
            throw new ArgumentException("Worker and worker type are required.");
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlTransaction transaction = connection.BeginTransaction();
        if (assignment.IsPrimary)
        {
            using NpgsqlCommand clear = connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = "update studio_job_worker_assignments set is_primary=false,updated_at_utc=now() where job_id=$1";
            clear.Parameters.AddWithValue(assignment.JobId);
            clear.ExecuteNonQuery();
        }
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into studio_job_worker_assignments
                (id,job_id,worker_user_name,worker_type_code,responsibility,is_primary,status,starts_at_utc,ends_at_utc,accepted_at_utc,completed_at_utc)
            values($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)
            on conflict(id) do update set worker_user_name=excluded.worker_user_name,
                worker_type_code=excluded.worker_type_code,responsibility=excluded.responsibility,
                is_primary=excluded.is_primary,status=excluded.status,starts_at_utc=excluded.starts_at_utc,
                ends_at_utc=excluded.ends_at_utc,accepted_at_utc=excluded.accepted_at_utc,
                completed_at_utc=excluded.completed_at_utc,updated_at_utc=now()
            """;
        foreach (object? value in new object?[]
        {
            assignment.Id, assignment.JobId, assignment.WorkerUserName.Trim(), assignment.NormalizedWorkerTypeCode,
            assignment.Responsibility.Trim(), assignment.IsPrimary, assignment.Status.Trim(),
            assignment.StartsAtUtc, assignment.EndsAtUtc, assignment.AcceptedAtUtc, assignment.CompletedAtUtc
        }) command.Parameters.AddWithValue(value ?? DBNull.Value);
        command.ExecuteNonQuery();
        transaction.Commit();
        return assignment;
    }

    public void SetStatus(Guid assignmentId, string status)
    {
        if (assignmentId == Guid.Empty || string.IsNullOrWhiteSpace(status))
            throw new ArgumentException("Assignment and status are required.");
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "update studio_job_worker_assignments set status=$2,accepted_at_utc=case when $2='Accepted' and accepted_at_utc is null then now() else accepted_at_utc end,completed_at_utc=case when $2='Completed' then coalesce(completed_at_utc,now()) else completed_at_utc end,updated_at_utc=now() where id=$1";
        command.Parameters.AddWithValue(assignmentId);
        command.Parameters.AddWithValue(status.Trim());
        if (command.ExecuteNonQuery() == 0) throw new KeyNotFoundException("Worker assignment was not found.");
    }

    public IReadOnlyList<WorkerAssignment> GetAssignments(Guid jobId)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select id,job_id,worker_user_name,worker_type_code,responsibility,is_primary,status,
                   starts_at_utc,ends_at_utc,accepted_at_utc,completed_at_utc
            from studio_job_worker_assignments where job_id=$1 order by is_primary desc,created_at_utc
            """;
        command.Parameters.AddWithValue(jobId);
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<WorkerAssignment> result = [];
        while (reader.Read())
            result.Add(new(reader.GetGuid(0),reader.GetGuid(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetBoolean(5),reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
                reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10)));
        return result;
    }
}
