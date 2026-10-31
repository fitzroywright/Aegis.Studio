using Aegis.Studio.Domain.Workers;
using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Workers;

public sealed class WorkerDirectory(StudioDatabase database)
{
    public IReadOnlyList<JobRole> GetTypes(bool includeInactive = false)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select code,display_name,can_be_primary,is_active from studio_worker_types where $1 or is_active order by display_name";
        command.Parameters.AddWithValue(includeInactive);
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<JobRole> result = [];
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3)));
        return result;
    }

    public void SaveJobRole(JobRole type)
    {
        if (string.IsNullOrWhiteSpace(type.Code) || string.IsNullOrWhiteSpace(type.DisplayName))
            throw new ArgumentException("Worker type code and display name are required.");
        string code = type.NormalizedCode;
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "insert into studio_worker_types(code,display_name,can_be_primary,is_active,updated_at_utc) values($1,$2,$3,$4,now()) on conflict(code) do update set display_name=excluded.display_name,can_be_primary=excluded.can_be_primary,is_active=excluded.is_active,updated_at_utc=now()";
        command.Parameters.AddWithValue(code);
        command.Parameters.AddWithValue(type.DisplayName.Trim());
        command.Parameters.AddWithValue(type.CanBePrimary);
        command.Parameters.AddWithValue(type.IsActive);
        command.ExecuteNonQuery();
    }

    public void SetJobRoleActive(string code, bool active)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Worker type code is required.");
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "update studio_worker_types set is_active=$2,updated_at_utc=now() where code=$1";
        command.Parameters.AddWithValue(code.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue(active);
        if (command.ExecuteNonQuery() == 0) throw new KeyNotFoundException("Worker type was not found.");
    }

    public IReadOnlyList<Worker> GetWorkers(string? jobRoleCode = null, bool includeInactive = false)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select user_name,display_name,worker_type_code,is_active,email,phone
            from studio_workers
            where ($1::text is null or worker_type_code=$1) and ($2 or is_active)
            order by display_name,user_name
            """;
        command.Parameters.AddWithValue((object?)jobRoleCode?.Trim().ToUpperInvariant() ?? DBNull.Value);
        command.Parameters.AddWithValue(includeInactive);
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<Worker> result = [];
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        return result;
    }

    public void SaveWorker(Worker worker)
    {
        if (string.IsNullOrWhiteSpace(worker.UserName) || string.IsNullOrWhiteSpace(worker.JobRoleCode))
            throw new ArgumentException("Worker name and job role are required.");
        using NpgsqlConnection connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into studio_workers(user_name,display_name,worker_type_code,is_active,email,phone,updated_at_utc)
            values($1,$2,$3,$4,$5,$6,now())
            on conflict(user_name) do update set display_name=excluded.display_name,worker_type_code=excluded.worker_type_code,
                is_active=excluded.is_active,email=excluded.email,phone=excluded.phone,updated_at_utc=now()
            """;
        command.Parameters.AddWithValue(worker.UserName.Trim());
        command.Parameters.AddWithValue(string.IsNullOrWhiteSpace(worker.DisplayName) ? worker.UserName.Trim() : worker.DisplayName.Trim());
        command.Parameters.AddWithValue(worker.NormalizedJobRoleCode);
        command.Parameters.AddWithValue(worker.IsActive);
        command.Parameters.AddWithValue((object?)worker.Email ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)worker.Phone ?? DBNull.Value);
        command.ExecuteNonQuery();
        using var membership = new NpgsqlCommand("insert into studio_worker_roles(user_name,role_code,is_active) values($1,$2,true) on conflict(user_name,role_code) do update set is_active=true,updated_at_utc=now()",connection,transaction);
        membership.Parameters.AddWithValue(worker.UserName.Trim());membership.Parameters.AddWithValue(worker.NormalizedJobRoleCode);membership.ExecuteNonQuery();
        transaction.Commit();
    }

    public IReadOnlyList<Worker> GetRoleMemberships(bool includeInactive = false)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            select w.user_name,w.display_name,r.role_code,w.is_active and r.is_active and t.is_active,w.email,w.phone
            from studio_workers w join studio_worker_roles r on r.user_name=w.user_name
            join studio_worker_types t on t.code=r.role_code
            where r.is_active and ($1 or (w.is_active and t.is_active)) order by w.display_name,r.role_code
            """;
        command.Parameters.AddWithValue(includeInactive);
        using var reader = command.ExecuteReader();
        List<Worker> result = [];
        while(reader.Read()) result.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetBoolean(3),reader.IsDBNull(4)?null:reader.GetString(4),reader.IsDBNull(5)?null:reader.GetString(5)));
        return result;
    }

    public void SaveRoles(string userName, string displayName, string? email, bool available, IReadOnlyList<string> codes)
    {
        string[] selected = codes.Select(code => code.Trim().ToUpperInvariant()).Distinct().ToArray();
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using(var guard = new NpgsqlCommand("select pg_advisory_xact_lock(hashtextextended($1,0))",connection,transaction))
        { guard.Parameters.AddWithValue("worker:"+userName.ToLowerInvariant()); guard.ExecuteNonQuery(); }
        using(var validate = new NpgsqlCommand("""
            select count(*) from studio_worker_types t where t.code=any($1) and
            (t.is_active or exists(select 1 from studio_worker_roles r where r.user_name=$2 and r.role_code=t.code and r.is_active))
            """,connection,transaction))
        {
            validate.Parameters.AddWithValue(selected);validate.Parameters.AddWithValue(userName);
            if(Convert.ToInt64(validate.ExecuteScalar()) != selected.Length) throw new ArgumentException("Choose active Job Roles. Existing inactive roles may be retained or removed.");
        }
        using(var worker = new NpgsqlCommand("""
            insert into studio_workers(user_name,display_name,worker_type_code,is_active,email)
            select $1,$2,$3,$4,$5 where $3::text is not null
            on conflict(user_name) do update set display_name=excluded.display_name,is_active=excluded.is_active,email=excluded.email,updated_at_utc=now()
            """,connection,transaction))
        {
            worker.Parameters.AddWithValue(userName);worker.Parameters.AddWithValue(displayName);
            worker.Parameters.AddWithValue((object?)selected.FirstOrDefault() ?? DBNull.Value);worker.Parameters.AddWithValue(available);worker.Parameters.AddWithValue((object?)email??DBNull.Value);worker.ExecuteNonQuery();
        }
        using(var clear = new NpgsqlCommand("update studio_worker_roles set is_active=false,updated_at_utc=now() where user_name=$1",connection,transaction))
        { clear.Parameters.AddWithValue(userName); clear.ExecuteNonQuery(); }
        foreach(string code in selected)
        {
            using var membership = new NpgsqlCommand("insert into studio_worker_roles(user_name,role_code,is_active) values($1,$2,true) on conflict(user_name,role_code) do update set is_active=true,updated_at_utc=now()",connection,transaction);
            membership.Parameters.AddWithValue(userName);membership.Parameters.AddWithValue(code);membership.ExecuteNonQuery();
        }
        // Existing photography integrations keep their profile/resource IDs and metadata.
        bool photographer = selected.Contains("PHOTOGRAPHER");
        if(photographer)
        {
            using var existingResource = new NpgsqlCommand("select schedule_resource_id from studio_photographers where lower(user_name)=lower($1)",connection,transaction);
            existingResource.Parameters.AddWithValue(userName);
            Guid resource = existingResource.ExecuteScalar() is Guid id ? id : Aegis.Studio.Web.Production.PhotographerStore.EnsureStaffResource(connection,transaction,userName,displayName);
            using var profile = new NpgsqlCommand("""
                insert into studio_photographers(user_name,display_name,employment_type,is_active,skills,schedule_resource_id,created_at_utc,updated_at_utc)
                values($1,$2,'Employee',$3,'{}',$4,now(),now())
                on conflict(user_name) do update set display_name=excluded.display_name,is_active=excluded.is_active,updated_at_utc=now()
                """,connection,transaction);
            profile.Parameters.AddWithValue(userName);profile.Parameters.AddWithValue(displayName);profile.Parameters.AddWithValue(available);profile.Parameters.AddWithValue(resource);profile.ExecuteNonQuery();
        }
        else
        {
            using var profile = new NpgsqlCommand("update studio_photographers set is_active=false,updated_at_utc=now() where lower(user_name)=lower($1)",connection,transaction);
            profile.Parameters.AddWithValue(userName);profile.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public WorkerAssignment Assign(WorkerAssignment assignment)
    {
        if (assignment.Id == Guid.Empty || assignment.JobId == Guid.Empty)
            throw new ArgumentException("A valid assignment and job are required.");
        if (string.IsNullOrWhiteSpace(assignment.WorkerUserName) || string.IsNullOrWhiteSpace(assignment.JobRoleCode))
            throw new ArgumentException("Worker and job role are required.");
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
            assignment.Id, assignment.JobId, assignment.WorkerUserName.Trim(), assignment.NormalizedJobRoleCode,
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


public sealed record WorkerRolesRequest(IReadOnlyList<string>? RoleCodes);
