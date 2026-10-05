using Aegis.Studio.Web.Persistence;
using Npgsql;

using Aegis.Studio.Web.Production;

namespace Aegis.Studio.Web.WorkDay;

public sealed class WorkDaySchedulingService : IWorkDaySchedulingService
{
    private readonly StudioDatabase database;
    private readonly PhotographerStore photographers;

    public WorkDaySchedulingService(StudioDatabase database, PhotographerStore photographers)
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
            if (assistant is null || !assistant.IsActive) throw new InvalidOperationException("Choose an active assistant profile.");
            if (string.Equals(assistant.UserName,photographer.UserName,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Photographer and assistant must be different people.");
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
        foreach(var person in new[]{photographer.UserName,request.AssistantUserName}.Where(name=>!string.IsNullOrWhiteSpace(name)))if(!photographers.IsAvailable(person!,request.CallTimeUtc,request.PlannedEndUtc))throw new InvalidOperationException(person+" is unavailable at that time.");
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

    public ShootSession Reschedule(Guid jobId,Guid shootId,IntegratedShootRequest request,string actor)
    {
        if(request.PlannedEndUtc<=request.CallTimeUtc)throw new InvalidOperationException("Shoot end must be after call time.");
        var photographer=photographers.Get(request.PhotographerUserName);
        if(photographer is null||!photographer.IsActive)throw new InvalidOperationException("Choose an active photographer.");
        var staff=new List<PhotographerProfile>{photographer};
        if(!string.IsNullOrWhiteSpace(request.AssistantUserName))
        {
            var assistant=photographers.Get(request.AssistantUserName);
            if(assistant is null||!assistant.IsActive||assistant.UserName.Equals(photographer.UserName,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Choose a different active assistant.");
            staff.Add(assistant);
        }
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        using(var job=new NpgsqlCommand("select id from studio_jobs where id=$1 and status not in ('Completed','Cancelled','Published') for update",c,tx))
        {job.Parameters.AddWithValue(jobId);if(job.ExecuteScalar() is null)throw new InvalidOperationException("Job is closed for scheduling.");}
        Guid schedule;string oldPhotographer;
        using(var find=new NpgsqlCommand("select schedule_entry_id,photographer_user_name,status,checked_in_at_utc from studio_shoot_sessions where id=$1 and job_id=$2 for update",c,tx))
        {
            find.Parameters.AddWithValue(shootId);find.Parameters.AddWithValue(jobId);using var r=find.ExecuteReader();
            if(!r.Read())throw new KeyNotFoundException("Shoot was not found.");
            if(r.IsDBNull(0)||r.GetString(2)=="Cancelled"||!r.IsDBNull(3))throw new InvalidOperationException("Only an unstarted scheduled shoot can be rescheduled.");
            schedule=r.GetGuid(0);oldPhotographer=r.GetString(1);
        }
        var next=staff.Select(p=>p.ScheduleResourceId).Concat(request.ResourceIds??[]).Distinct().Order().ToArray();
        Guid[] previous;
        using(var resources=new NpgsqlCommand("select coalesce(array_agg(resource_id),'{}'::uuid[]) from studio_schedule_entry_resources where entry_id=$1",c,tx))
        {resources.Parameters.AddWithValue(schedule);previous=(Guid[])resources.ExecuteScalar()!;}
        foreach(var resource in next.Concat(previous).Distinct().Order())
        {using var guard=new NpgsqlCommand("select pg_advisory_xact_lock(hashtextextended($1,0))",c,tx);guard.Parameters.AddWithValue(resource.ToString("D"));guard.ExecuteNonQuery();}
        foreach(var person in staff)if(!photographers.IsAvailable(person.UserName,request.CallTimeUtc,request.PlannedEndUtc))throw new InvalidOperationException(person.DisplayName+" is unavailable at that time.");
        EnsureNoConflicts(c,tx,next,request.CallTimeUtc,request.PlannedEndUtc,schedule);
        Sql("update studio_schedule_entries set starts_at_utc=$2,ends_at_utc=$3,location=$4,notes=$5,status='Scheduled' where id=$1",schedule,request.CallTimeUtc.ToUniversalTime(),request.PlannedEndUtc.ToUniversalTime(),request.Location,request.CoverageNotes);
        Sql("delete from studio_schedule_entry_resources where entry_id=$1",schedule);
        foreach(var resource in next)Sql("insert into studio_schedule_entry_resources values($1,$2)",schedule,resource);
        Sql("""
            update studio_shoot_sessions set photographer_user_name=$2,assistant_user_name=$3,call_time_utc=$4,planned_end_utc=$5,
              location=$6,coverage_notes=$7,equipment_notes=$8,updated_at_utc=now() where id=$1
            """,shootId,photographer.UserName,request.AssistantUserName,request.CallTimeUtc.ToUniversalTime(),request.PlannedEndUtc.ToUniversalTime(),request.Location,request.CoverageNotes,request.EquipmentNotes);
        Sql("update studio_jobs set assigned_to=$2 where id=$1 and (assigned_to is null or lower(assigned_to)=lower($3))",jobId,photographer.UserName,oldPhotographer);
        Sql("insert into studio_schedule_assignment_history(id,entry_id,job_id,changed_by,changed_at_utc,previous_resource_ids,new_resource_ids,reason) values($1,$2,$3,$4,now(),$5,$6,$7)",Guid.NewGuid(),schedule,jobId,actor,previous,next,"Studio embedded reschedule / photographer change");
        Sql("insert into studio_operational_audit(id,job_id,activity_id,action,user_name,detail,created_at_utc) values($1,$2,null,'ShootRescheduled',$3,$4,now())",Guid.NewGuid(),jobId,actor,$"{oldPhotographer} → {photographer.UserName}, {request.CallTimeUtc:u} to {request.PlannedEndUtc:u}; schedule {schedule}");
        tx.Commit();return new OperationalWorkStore(database).GetShoots(jobId).Single(s=>s.Id==shootId);
        void Sql(string sql,params object?[] values){using var cmd=new NpgsqlCommand(sql,c,tx);foreach(var v in values)cmd.Parameters.AddWithValue(v??DBNull.Value);cmd.ExecuteNonQuery();}
    }

    public void Cancel(Guid jobId,Guid shootId,string actor)
    {
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        using(var guard=new NpgsqlCommand("select id from studio_jobs where id=$1 and status not in ('Completed','Cancelled','Published') for update",c,tx)){guard.Parameters.AddWithValue(jobId);if(guard.ExecuteScalar() is null)throw new InvalidOperationException("Job is closed for scheduling.");}
        Guid entry;using(var shoot=new NpgsqlCommand("select schedule_entry_id,status,checked_in_at_utc from studio_shoot_sessions where id=$1 and job_id=$2 for update",c,tx))
        {shoot.Parameters.AddWithValue(shootId);shoot.Parameters.AddWithValue(jobId);using var r=shoot.ExecuteReader();if(!r.Read())throw new KeyNotFoundException("Shoot not found.");if(r.GetString(1)=="Cancelled")return;if(!r.IsDBNull(2)||r.IsDBNull(0))throw new InvalidOperationException("Only an unstarted shoot can be cancelled.");entry=r.GetGuid(0);}
        using(var update=new NpgsqlCommand("update studio_schedule_entries set status='Cancelled' where id=$1",c,tx)){update.Parameters.AddWithValue(entry);update.ExecuteNonQuery();}
        using(var update=new NpgsqlCommand("update studio_shoot_sessions set status='Cancelled',updated_at_utc=now() where id=$1",c,tx)){update.Parameters.AddWithValue(shootId);update.ExecuteNonQuery();}
        using(var update=new NpgsqlCommand("update studio_jobs set status='Needs Scheduling' where id=$1 and status in ('Scheduled','Confirmed') and not exists(select 1 from studio_shoot_sessions where job_id=$1 and status<>'Cancelled')",c,tx)){update.Parameters.AddWithValue(jobId);update.ExecuteNonQuery();}
        using(var audit=new NpgsqlCommand("insert into studio_operational_audit(id,job_id,action,user_name,detail,created_at_utc) values($1,$2,'ShootCancelled',$3,$4,now())",c,tx)){audit.Parameters.AddWithValue(Guid.NewGuid());audit.Parameters.AddWithValue(jobId);audit.Parameters.AddWithValue(actor);audit.Parameters.AddWithValue($"Shoot {shootId}; authoritative schedule {entry} cancelled.");audit.ExecuteNonQuery();}
        tx.Commit();
    }

    private static void EnsureNoConflicts(NpgsqlConnection connection, NpgsqlTransaction transaction, IReadOnlyList<Guid> resources, DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc, Guid? excludingEntry=null)
    {
        if (resources.Count == 0) return;
        using NpgsqlCommand command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            select r.name,e.title,e.starts_at_utc,e.ends_at_utc
            from studio_schedule_entries e
            join studio_schedule_entry_resources er on er.entry_id=e.id
            join studio_schedule_resources r on r.id=er.resource_id
            where er.resource_id=any($1) and e.starts_at_utc<$3 and e.ends_at_utc>$2 and e.status<>'Cancelled' and ($4::uuid is null or e.id<>$4)
            limit 1
            """;
        command.Parameters.AddWithValue(resources.ToArray()); command.Parameters.AddWithValue(startsAtUtc.ToUniversalTime()); command.Parameters.AddWithValue(endsAtUtc.ToUniversalTime()); command.Parameters.AddWithValue((object?)excludingEntry??DBNull.Value);
        using NpgsqlDataReader reader = command.ExecuteReader();
        if (reader.Read()) throw new InvalidOperationException($"Schedule conflict: {reader.GetString(0)} is already assigned to {reader.GetString(1)} from {reader.GetFieldValue<DateTimeOffset>(2):u} to {reader.GetFieldValue<DateTimeOffset>(3):u}.");
    }
}

public interface IWorkDaySchedulingService
{
    ShootSession Create(Guid jobId,IntegratedShootRequest request,string actor);
    ShootSession Reschedule(Guid jobId,Guid shootId,IntegratedShootRequest request,string actor);
}
