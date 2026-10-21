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
