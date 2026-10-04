using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Production;

public sealed class ShootCoordinator
{
    private readonly StudioDatabase database;
    private readonly PhotographerStore photographers;

    public ShootCoordinator(StudioDatabase database, PhotographerStore photographers)
    {
        this.database = database;
        this.photographers = photographers;
    }

    public ShootSession Create(Guid jobId, IntegratedShootRequest request, string userName)
    {
        if (request.PlannedEndUtc <= request.CallTimeUtc) throw new InvalidOperationException("Shoot end must be after call time.");
        PhotographerProfile photographer = photographers.Get(request.PhotographerUserName) ?? throw new InvalidOperationException("Photographer must have an active photographer profile before assignment.");
        if (!photographer.IsActive) throw new InvalidOperationException("Photographer profile is inactive.");
        if (!photographers.IsAvailable(photographer.UserName, request.CallTimeUtc, request.PlannedEndUtc)) throw new InvalidOperationException("Photographer is unavailable for the requested time.");

        List<Guid> resources = [photographer.ScheduleResourceId];
        if (!string.IsNullOrWhiteSpace(request.AssistantUserName))
        {
            PhotographerProfile? assistant = photographers.Get(request.AssistantUserName);
            if (assistant is not null)
            {
                if (!photographers.IsAvailable(assistant.UserName, request.CallTimeUtc, request.PlannedEndUtc)) throw new InvalidOperationException("Assistant is unavailable for the requested time.");
                resources.Add(assistant.ScheduleResourceId);
            }
        }
        resources.AddRange(request.ResourceIds ?? []);
        resources = resources.Distinct().ToList();

        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlTransaction transaction = connection.BeginTransaction();
        using(var jobLock=new NpgsqlCommand("select id from studio_jobs where id=$1 and status not in ('Completed','Cancelled','Published') for update",connection,transaction))
        {jobLock.Parameters.AddWithValue(jobId);if(jobLock.ExecuteScalar() is null)throw new InvalidOperationException("The job is unavailable for scheduling.");}
        using(var duplicate=new NpgsqlCommand("select id from studio_shoot_sessions where job_id=$1 and lower(photographer_user_name)=lower($2) and call_time_utc=$3 and planned_end_utc=$4 and status<>'Cancelled'",connection,transaction))
        {
            duplicate.Parameters.AddWithValue(jobId);duplicate.Parameters.AddWithValue(photographer.UserName);duplicate.Parameters.AddWithValue(request.CallTimeUtc.ToUniversalTime());duplicate.Parameters.AddWithValue(request.PlannedEndUtc.ToUniversalTime());
            if(duplicate.ExecuteScalar() is Guid existing)return new OperationalWorkStore(database).GetShoots(jobId).Single(x=>x.Id==existing);
        }
        foreach(Guid resourceId in resources.OrderBy(x=>x))
        {
            using var resourceLock=new NpgsqlCommand("select pg_advisory_xact_lock(hashtextextended($1,0))",connection,transaction);
            resourceLock.Parameters.AddWithValue(resourceId.ToString("D"));resourceLock.ExecuteNonQuery();
        }
        EnsureNoConflicts(connection, transaction, resources, request.CallTimeUtc, request.PlannedEndUtc);

        Guid scheduleId = Guid.NewGuid();
        using(var requested=new NpgsqlCommand("select id from studio_schedule_entries where job_id=$1 and status='Requested' and not exists(select 1 from studio_shoot_sessions where schedule_entry_id=studio_schedule_entries.id) order by created_at_utc limit 1 for update",connection,transaction))
        {requested.Parameters.AddWithValue(jobId);if(requested.ExecuteScalar() is Guid planned)scheduleId=planned;}
        using (NpgsqlCommand schedule = connection.CreateCommand())
        {
            schedule.Transaction = transaction;
            schedule.CommandText = "insert into studio_schedule_entries(id,job_id,booking_id,title,starts_at_utc,ends_at_utc,location,status,notes,created_at_utc) values($1,$2,null,$3,$4,$5,$6,'Scheduled',$7,now()) on conflict(id) do update set title=excluded.title,starts_at_utc=excluded.starts_at_utc,ends_at_utc=excluded.ends_at_utc,location=excluded.location,status='Scheduled',notes=excluded.notes";
            schedule.Parameters.AddWithValue(scheduleId); schedule.Parameters.AddWithValue(jobId); schedule.Parameters.AddWithValue("Photography shoot");
            schedule.Parameters.AddWithValue(request.CallTimeUtc.ToUniversalTime()); schedule.Parameters.AddWithValue(request.PlannedEndUtc.ToUniversalTime());
            schedule.Parameters.AddWithValue((object?)request.Location?.Trim() ?? DBNull.Value); schedule.Parameters.AddWithValue((object?)request.CoverageNotes?.Trim() ?? DBNull.Value); schedule.ExecuteNonQuery();
        }
        foreach (Guid resourceId in resources)
        {
            using NpgsqlCommand resource = connection.CreateCommand(); resource.Transaction = transaction;
            resource.CommandText = "insert into studio_schedule_entry_resources(entry_id,resource_id) values($1,$2)";
            resource.Parameters.AddWithValue(scheduleId); resource.Parameters.AddWithValue(resourceId); resource.ExecuteNonQuery();
        }

        Guid shootId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using (NpgsqlCommand shoot = connection.CreateCommand())
        {
            shoot.Transaction = transaction;
            shoot.CommandText = """
                insert into studio_shoot_sessions(id,job_id,schedule_entry_id,photographer_user_name,assistant_user_name,call_time_utc,planned_end_utc,location,status,checked_in_at_utc,checked_out_at_utc,image_count,backup_confirmed,reshoot_required,coverage_notes,equipment_notes,handoff_status,handoff_to,created_at_utc,updated_at_utc)
                values($1,$2,$3,$4,$5,$6,$7,$8,'Scheduled',null,null,0,false,false,$9,$10,'NotReady',null,$11,$11)
                """;
            object?[] values=[shootId,jobId,scheduleId,photographer.UserName,(object?)request.AssistantUserName?.Trim()??DBNull.Value,request.CallTimeUtc.ToUniversalTime(),request.PlannedEndUtc.ToUniversalTime(),(object?)request.Location?.Trim()??DBNull.Value,(object?)request.CoverageNotes?.Trim()??DBNull.Value,(object?)request.EquipmentNotes?.Trim()??DBNull.Value,now];
            foreach(object? value in values) shoot.Parameters.AddWithValue(value??DBNull.Value); shoot.ExecuteNonQuery();
        }
        using (NpgsqlCommand audit = connection.CreateCommand())
        {
            audit.Transaction = transaction; audit.CommandText="insert into studio_operational_audit(id,job_id,activity_id,action,user_name,detail,created_at_utc) values($1,$2,null,'ShootScheduled',$3,$4,now())";
            audit.Parameters.AddWithValue(Guid.NewGuid()); audit.Parameters.AddWithValue(jobId); audit.Parameters.AddWithValue(userName); audit.Parameters.AddWithValue($"{photographer.DisplayName} at {request.CallTimeUtc:u}; schedule {scheduleId}"); audit.ExecuteNonQuery();
        }
        using(var jobUpdate=new NpgsqlCommand("update studio_jobs set assigned_to=coalesce(nullif(assigned_to,''),$2),status=case when status in ('New','Needs Scheduling','Confirmed') then 'Scheduled' else status end where id=$1",connection,transaction))
        {jobUpdate.Parameters.AddWithValue(jobId);jobUpdate.Parameters.AddWithValue(photographer.UserName);jobUpdate.ExecuteNonQuery();}
        transaction.Commit();
        return new OperationalWorkStore(database).GetShoots(jobId).Single(x => x.Id == shootId);
    }

    private static void EnsureNoConflicts(NpgsqlConnection connection, NpgsqlTransaction transaction, IReadOnlyList<Guid> resources, DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc)
    {
        if (resources.Count == 0) return;
        using NpgsqlCommand command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            select r.name,e.title,e.starts_at_utc,e.ends_at_utc
            from studio_schedule_entries e
            join studio_schedule_entry_resources er on er.entry_id=e.id
            join studio_schedule_resources r on r.id=er.resource_id
            where er.resource_id=any($1) and e.starts_at_utc<$3 and e.ends_at_utc>$2 and e.status<>'Cancelled'
            limit 1
            """;
        command.Parameters.AddWithValue(resources.ToArray()); command.Parameters.AddWithValue(startsAtUtc.ToUniversalTime()); command.Parameters.AddWithValue(endsAtUtc.ToUniversalTime());
        using NpgsqlDataReader reader = command.ExecuteReader();
        if (reader.Read()) throw new InvalidOperationException($"Schedule conflict: {reader.GetString(0)} is already assigned to {reader.GetString(1)} from {reader.GetFieldValue<DateTimeOffset>(2):u} to {reader.GetFieldValue<DateTimeOffset>(3):u}.");
    }
}

public sealed record IntegratedShootRequest(string PhotographerUserName,string? AssistantUserName,DateTimeOffset CallTimeUtc,DateTimeOffset PlannedEndUtc,string? Location,string? CoverageNotes,string? EquipmentNotes,IReadOnlyList<Guid>? ResourceIds);
