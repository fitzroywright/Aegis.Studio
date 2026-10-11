using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Scheduling;

public sealed class ScheduleStore
{
    private readonly StudioDatabase database;
    private readonly IConfiguration configuration;

    public ScheduleStore(StudioDatabase database, IConfiguration configuration)
    {
        this.database = database;
        this.configuration = configuration;
    }

    public void EnsureOperationalSchema()
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            create table if not exists studio_schedule_assignment_history(
              id uuid primary key,
              entry_id uuid not null,
              job_id uuid null,
              changed_by text not null,
              changed_at_utc timestamptz not null,
              previous_resource_ids uuid[] not null,
              new_resource_ids uuid[] not null,
              reason text null);
            create index if not exists ix_schedule_assignment_history_entry
              on studio_schedule_assignment_history(entry_id,changed_at_utc desc);
            """;
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<ScheduleResource> GetResources(bool includeInactive = false)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id,name,resource_type,is_active,notes,created_at_utc from studio_schedule_resources where $1 or is_active order by resource_type,name";
        command.Parameters.AddWithValue(includeInactive);
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<ScheduleResource> values = [];
        while (reader.Read()) values.Add(ReadResource(reader));
        return values;
    }

    public ScheduleResource SaveResource(ScheduleResourceRequest request, Guid? id = null)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw new InvalidOperationException("Resource name is required.");
        string type = string.IsNullOrWhiteSpace(request.ResourceType) ? "Staff" : request.ResourceType.Trim();
        ScheduleResource value = new(id ?? Guid.NewGuid(), request.Name.Trim(), type, request.IsActive, request.Notes?.Trim(), DateTimeOffset.UtcNow);
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            insert into studio_schedule_resources(id,name,resource_type,is_active,notes,created_at_utc)
            values ($1,$2,$3,$4,$5,$6)
            on conflict(id) do update set name=excluded.name,resource_type=excluded.resource_type,is_active=excluded.is_active,notes=excluded.notes
            """;
        command.Parameters.AddWithValue(value.Id); command.Parameters.AddWithValue(value.Name); command.Parameters.AddWithValue(value.ResourceType); command.Parameters.AddWithValue(value.IsActive); command.Parameters.AddWithValue((object?)value.Notes ?? DBNull.Value); command.Parameters.AddWithValue(value.CreatedAtUtc); command.ExecuteNonQuery();
        return value;
    }

    public IReadOnlyList<ScheduleEntry> GetEntries(DateTimeOffset? from = null, DateTimeOffset? to = null, Guid? jobId = null,Guid? entryId=null)
    {
        DateTimeOffset start = from?.ToUniversalTime() ?? DateTimeOffset.UtcNow.AddDays(-30);
        DateTimeOffset end = to?.ToUniversalTime() ?? DateTimeOffset.UtcNow.AddDays(180);
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select e.id,e.job_id,e.booking_id,e.title,e.starts_at_utc,e.ends_at_utc,e.location,e.status,e.notes,e.created_at_utc,
                   coalesce(array_agg(er.resource_id) filter (where er.resource_id is not null),'{}')
            from studio_schedule_entries e
            left join studio_schedule_entry_resources er on er.entry_id=e.id
            where ($4::uuid is not null or (e.starts_at_utc < $2 and e.ends_at_utc > $1)) and ($3::uuid is null or e.job_id=$3) and ($4::uuid is null or e.id=$4)
            group by e.id
            order by e.starts_at_utc
            """;
        command.Parameters.AddWithValue(start); command.Parameters.AddWithValue(end); command.Parameters.AddWithValue((object?)jobId??DBNull.Value);command.Parameters.AddWithValue((object?)entryId??DBNull.Value);
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<ScheduleEntry> values = [];
        while (reader.Read()) values.Add(ReadEntry(reader));
        return values;
    }

    public ScheduleEntry Create(ScheduleEntryRequest request)
    {
        ValidateEntry(request);
        Guid id = Guid.NewGuid();
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlTransaction transaction = connection.BeginTransaction();
        LockScheduleResources(connection,transaction,request.ResourceIds);
        IReadOnlyList<ScheduleConflict> conflicts = FindConflicts(request.StartsAtUtc, request.EndsAtUtc, request.ResourceIds, null);
        if (conflicts.Count > 0 && !request.AllowConflicts) throw new ScheduleConflictException(conflicts);
        using (NpgsqlCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "insert into studio_schedule_entries(id,job_id,booking_id,title,starts_at_utc,ends_at_utc,location,status,notes,created_at_utc) values ($1,$2,$3,$4,$5,$6,$7,$8,$9,now())";
            AddEntryParameters(command, id, request);
            command.ExecuteNonQuery();
        }
        SaveResources(connection, transaction, id, request.ResourceIds);
        transaction.Commit();
        return GetEntries(request.StartsAtUtc.AddMinutes(-1), request.EndsAtUtc.AddMinutes(1)).First(x => x.Id == id);
    }

    public ScheduleEntry Update(Guid id, ScheduleEntryRequest request)
    {
        ValidateEntry(request);
        if(ChangeLinkedShoot(id,request.ResourceIds??[],request.StartsAtUtc,request.EndsAtUtc,request.Location,request.Notes,request.JobId,status:request.Status??"Scheduled"))
            return GetEntries(request.StartsAtUtc.AddMinutes(-1),request.EndsAtUtc.AddMinutes(1)).Single(e=>e.Id==id);
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlTransaction transaction = connection.BeginTransaction();
        Guid[] existing=GetAssignedResources(connection,transaction,id);
        LockScheduleResources(connection,transaction,existing.Concat(request.ResourceIds??[]));
        IReadOnlyList<ScheduleConflict> conflicts = FindConflicts(request.StartsAtUtc, request.EndsAtUtc, request.ResourceIds, id);
        if (conflicts.Count > 0 && !request.AllowConflicts) throw new ScheduleConflictException(conflicts);
        using (NpgsqlCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "update studio_schedule_entries set job_id=$2,booking_id=$3,title=$4,starts_at_utc=$5,ends_at_utc=$6,location=$7,status=$8,notes=$9 where id=$1";
            AddEntryParameters(command, id, request);
            if (command.ExecuteNonQuery() == 0) throw new KeyNotFoundException("Schedule entry was not found.");
        }
        using (NpgsqlCommand delete = connection.CreateCommand()) { delete.Transaction = transaction; delete.CommandText = "delete from studio_schedule_entry_resources where entry_id=$1"; delete.Parameters.AddWithValue(id); delete.ExecuteNonQuery(); }
        SaveResources(connection, transaction, id, request.ResourceIds);
        transaction.Commit();
        return GetEntries(request.StartsAtUtc.AddMinutes(-1), request.EndsAtUtc.AddMinutes(1)).First(x => x.Id == id);
    }

    // Shared with WorkflowStore.SaveSession: resource-specific transaction advisory locks.
    // The conflict query runs only after all locks have been acquired.
    private static void LockScheduleResources(NpgsqlConnection connection,NpgsqlTransaction tx,IEnumerable<Guid>? ids)
    {
        foreach(Guid id in (ids??[]).Distinct().Order())
        {
            using var cmd=new NpgsqlCommand("select pg_advisory_xact_lock(hashtextextended($1,0))",connection,tx);
            cmd.Parameters.AddWithValue(id.ToString());
            cmd.ExecuteNonQuery();
        }
    }
    private static Guid[] GetAssignedResources(NpgsqlConnection connection,NpgsqlTransaction tx,Guid entryId)
    {
        using var cmd=new NpgsqlCommand("select resource_id from studio_schedule_entry_resources where entry_id=$1",connection,tx);
        cmd.Parameters.AddWithValue(entryId);
        using var reader=cmd.ExecuteReader();
        var ids=new List<Guid>();
        while(reader.Read())ids.Add(reader.GetGuid(0));
        return ids.ToArray();
    }

    public IReadOnlyList<ScheduleConflict> FindConflicts(DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc, IReadOnlyList<Guid>? resourceIds, Guid? excludingEntryId)
    {
        if (resourceIds is null || resourceIds.Count == 0) return [];
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select e.id,e.title,r.id,r.name,e.starts_at_utc,e.ends_at_utc
            from studio_schedule_entries e
            join studio_schedule_entry_resources er on er.entry_id=e.id
            join studio_schedule_resources r on r.id=er.resource_id
            where er.resource_id = any($1)
              and e.starts_at_utc < $3 and e.ends_at_utc > $2
              and ($4::uuid is null or e.id <> $4)
              and e.status <> 'Cancelled'
            order by e.starts_at_utc
            """;
        command.Parameters.AddWithValue(resourceIds.ToArray()); command.Parameters.AddWithValue(startsAtUtc.ToUniversalTime()); command.Parameters.AddWithValue(endsAtUtc.ToUniversalTime()); command.Parameters.AddWithValue((object?)excludingEntryId ?? DBNull.Value);
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<ScheduleConflict> values = [];
        while (reader.Read()) values.Add(new ScheduleConflict(reader.GetGuid(0),reader.GetString(1),reader.GetGuid(2),reader.GetString(3),reader.GetFieldValue<DateTimeOffset>(4),reader.GetFieldValue<DateTimeOffset>(5)));
        return values;
    }

    public IReadOnlyList<StaffCapacitySnapshot> GetStaffCapacity(DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from) throw new InvalidOperationException("Capacity end must be after start.");
        DateTimeOffset start=from.ToUniversalTime(), end=to.ToUniversalTime();
        double days=Math.Max(1d,(end-start).TotalDays);
        double dailyHours=Math.Clamp(configuration.GetValue<double?>("Scheduling:DefaultDailyCapacityHours")??8d,1d,24d);
        double capacityMinutes=days*dailyHours*60d;

        using NpgsqlConnection connection=database.OpenConnection();
        using NpgsqlCommand command=connection.CreateCommand();
        command.CommandText="""
            select p.user_name,p.display_name,p.schedule_resource_id,
                   coalesce(sum(extract(epoch from (least(e.ends_at_utc,$2)-greatest(e.starts_at_utc,$1)))/60.0)
                       filter(where e.id is not null and e.status<>'Cancelled'),0),
                   coalesce((select sum(extract(epoch from (least(a.ends_at_utc,$2)-greatest(a.starts_at_utc,$1)))/60.0)
                       from studio_photographer_availability a
                       where lower(a.user_name)=lower(p.user_name)
                         and a.availability_type in ('Unavailable','Leave')
                         and a.starts_at_utc<$2 and a.ends_at_utc>$1),0)
            from studio_photographers p
            left join studio_schedule_entry_resources er on er.resource_id=p.schedule_resource_id
            left join studio_schedule_entries e on e.id=er.entry_id and e.starts_at_utc<$2 and e.ends_at_utc>$1
            where p.is_active
            group by p.user_name,p.display_name,p.schedule_resource_id
            order by p.display_name,p.user_name
            """;
        command.Parameters.AddWithValue(start);command.Parameters.AddWithValue(end);
        using NpgsqlDataReader r=command.ExecuteReader();
        List<StaffCapacitySnapshot> values=[];
        while(r.Read())
        {
            double scheduled=Convert.ToDouble(r.GetValue(3),System.Globalization.CultureInfo.InvariantCulture);
            double unavailable=Convert.ToDouble(r.GetValue(4),System.Globalization.CultureInfo.InvariantCulture);
            double effective=Math.Max(0d,capacityMinutes-unavailable);
            double remaining=Math.Max(0d,effective-scheduled);
            double utilization=effective<=0d?(scheduled>0?100d:0d):(scheduled/effective*100d);
            string state=effective<=0d?"Unavailable":scheduled>effective?"OverCapacity":utilization>=85d?"NearCapacity":"Available";
            values.Add(new(r.GetString(0),r.GetString(1),r.GetGuid(2),start,end,
                Math.Round(effective,1),Math.Round(scheduled,1),Math.Round(unavailable,1),Math.Round(remaining,1),Math.Round(utilization,1),state));
        }
        return values;
    }

    public ScheduleAssignmentChange Reassign(
        Guid entryId,
        IReadOnlyList<Guid> newResourceIds,
        string changedBy,
        string? reason)
    {
        ScheduleEntry current=GetEntries(DateTimeOffset.UtcNow.AddYears(-10),DateTimeOffset.UtcNow.AddYears(10))
            .FirstOrDefault(x=>x.Id==entryId)??throw new KeyNotFoundException("Schedule entry was not found.");
        Guid[] next=(newResourceIds??[]).Distinct().ToArray();
        if(ChangeLinkedShoot(entryId,next,current.StartsAtUtc,current.EndsAtUtc,current.Location,current.Notes,current.JobId,changedBy))
            return GetAssignmentHistory(entryId,1).Single();
        using NpgsqlConnection connection=database.OpenConnection();
        using NpgsqlTransaction tx=connection.BeginTransaction();
        Guid[] previous=GetAssignedResources(connection,tx,entryId);
        LockScheduleResources(connection,tx,previous.Concat(next));
        IReadOnlyList<ScheduleConflict> conflicts=FindConflicts(current.StartsAtUtc,current.EndsAtUtc,next,entryId);
        if(conflicts.Count>0) throw new ScheduleConflictException(conflicts);
        using(NpgsqlCommand delete=connection.CreateCommand())
        {
            delete.Transaction=tx;delete.CommandText="delete from studio_schedule_entry_resources where entry_id=$1";delete.Parameters.AddWithValue(entryId);delete.ExecuteNonQuery();
        }
        SaveResources(connection,tx,entryId,next);
        Guid historyId=Guid.NewGuid();DateTimeOffset changedAt=DateTimeOffset.UtcNow;
        using(NpgsqlCommand audit=connection.CreateCommand())
        {
            audit.Transaction=tx;
            audit.CommandText="insert into studio_schedule_assignment_history(id,entry_id,job_id,changed_by,changed_at_utc,previous_resource_ids,new_resource_ids,reason) values($1,$2,$3,$4,$5,$6,$7,$8)";
            audit.Parameters.AddWithValue(historyId);audit.Parameters.AddWithValue(entryId);audit.Parameters.AddWithValue((object?)current.JobId??DBNull.Value);
            audit.Parameters.AddWithValue(changedBy);audit.Parameters.AddWithValue(changedAt);audit.Parameters.AddWithValue(current.ResourceIds.ToArray());audit.Parameters.AddWithValue(next);audit.Parameters.AddWithValue((object?)reason?.Trim()??DBNull.Value);
            audit.ExecuteNonQuery();
        }
        tx.Commit();
        return new(historyId,entryId,current.JobId,changedBy,changedAt,current.ResourceIds.ToArray(),next,reason?.Trim());
    }

    public IReadOnlyList<ScheduleAssignmentChange> GetAssignmentHistory(Guid entryId,int count=100)
    {
        using NpgsqlConnection connection=database.OpenConnection();using NpgsqlCommand q=connection.CreateCommand();
        q.CommandText="select id,entry_id,job_id,changed_by,changed_at_utc,previous_resource_ids,new_resource_ids,reason from studio_schedule_assignment_history where entry_id=$1 order by changed_at_utc desc limit $2";
        q.Parameters.AddWithValue(entryId);q.Parameters.AddWithValue(Math.Clamp(count,1,500));
        using NpgsqlDataReader r=q.ExecuteReader();List<ScheduleAssignmentChange> values=[];
        while(r.Read())values.Add(new(r.GetGuid(0),r.GetGuid(1),r.IsDBNull(2)?null:r.GetGuid(2),r.GetString(3),r.GetFieldValue<DateTimeOffset>(4),r.GetFieldValue<Guid[]>(5),r.GetFieldValue<Guid[]>(6),r.IsDBNull(7)?null:r.GetString(7)));
        return values;
    }

    private bool ChangeLinkedShoot(Guid entry,IReadOnlyList<Guid> resources,DateTimeOffset start,DateTimeOffset end,string? location,string? notes,Guid? jobId,string actor="Studio scheduling",string status="Scheduled")
    {
        using var c=database.OpenConnection();using var cmd=new NpgsqlCommand("select id,job_id,photographer_user_name,assistant_user_name,equipment_notes from studio_shoot_sessions where schedule_entry_id=$1 and status<>'Cancelled'",c);cmd.Parameters.AddWithValue(entry);
        Guid id,job;string photographer;string? assistant,equipment;
        using(var r=cmd.ExecuteReader()){if(!r.Read())return false;id=r.GetGuid(0);job=r.GetGuid(1);photographer=r.GetString(2);assistant=r.IsDBNull(3)?null:r.GetString(3);equipment=r.IsDBNull(4)?null:r.GetString(4);}
        if(jobId!=job)throw new InvalidOperationException("A linked shoot cannot be moved to another job.");
        var store=new Aegis.Studio.Web.Production.PhotographerStore(database);
        if(status=="Cancelled"){new Aegis.Studio.Web.WorkDay.WorkDaySchedulingService(database,store).Cancel(job,id,actor);return true;}
        if(status!="Scheduled")throw new InvalidOperationException("Use Scheduled or Cancelled for an unstarted photography assignment; field completion comes from WorkDay.");
        var people=store.GetByScheduleResourceIds(resources).Where(p=>p.IsActive).ToArray();
        var primary=people.FirstOrDefault(p=>p.UserName.Equals(photographer,StringComparison.OrdinalIgnoreCase))??people.FirstOrDefault()??throw new InvalidOperationException("A photography assignment requires an active photographer.");
        var secondary=people.FirstOrDefault(p=>!p.UserName.Equals(primary.UserName,StringComparison.OrdinalIgnoreCase)&&p.UserName.Equals(assistant,StringComparison.OrdinalIgnoreCase))??people.FirstOrDefault(p=>!p.UserName.Equals(primary.UserName,StringComparison.OrdinalIgnoreCase));
        new Aegis.Studio.Web.WorkDay.WorkDaySchedulingService(database,store).Reschedule(job,id,
            new(primary.UserName,secondary?.UserName,start,end,location,notes,equipment,resources),actor);
        return true;
    }

    private static void ValidateEntry(ScheduleEntryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title)) throw new InvalidOperationException("Schedule title is required.");
        if (request.EndsAtUtc <= request.StartsAtUtc) throw new InvalidOperationException("Schedule end must be after start.");
    }

    private static void AddEntryParameters(NpgsqlCommand command, Guid id, ScheduleEntryRequest request)
    {
        command.Parameters.AddWithValue(id); command.Parameters.AddWithValue((object?)request.JobId ?? DBNull.Value); command.Parameters.AddWithValue((object?)request.BookingId ?? DBNull.Value); command.Parameters.AddWithValue(request.Title.Trim()); command.Parameters.AddWithValue(request.StartsAtUtc.ToUniversalTime()); command.Parameters.AddWithValue(request.EndsAtUtc.ToUniversalTime()); command.Parameters.AddWithValue((object?)request.Location?.Trim() ?? DBNull.Value); command.Parameters.AddWithValue(string.IsNullOrWhiteSpace(request.Status) ? "Scheduled" : request.Status.Trim()); command.Parameters.AddWithValue((object?)request.Notes?.Trim() ?? DBNull.Value);
    }

    private static void SaveResources(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid entryId, IReadOnlyList<Guid>? resources)
    {
        foreach (Guid resourceId in (resources ?? []).Distinct())
        {
            using NpgsqlCommand command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "insert into studio_schedule_entry_resources(entry_id,resource_id) values ($1,$2)"; command.Parameters.AddWithValue(entryId); command.Parameters.AddWithValue(resourceId); command.ExecuteNonQuery();
        }
    }

    private static ScheduleResource ReadResource(NpgsqlDataReader r) => new(r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetBoolean(3),r.IsDBNull(4)?null:r.GetString(4),r.GetFieldValue<DateTimeOffset>(5));
    private static ScheduleEntry ReadEntry(NpgsqlDataReader r) => new(r.GetGuid(0),r.IsDBNull(1)?null:r.GetGuid(1),r.IsDBNull(2)?null:r.GetGuid(2),r.GetString(3),r.GetFieldValue<DateTimeOffset>(4),r.GetFieldValue<DateTimeOffset>(5),r.IsDBNull(6)?null:r.GetString(6),r.GetString(7),r.IsDBNull(8)?null:r.GetString(8),r.GetFieldValue<DateTimeOffset>(9),r.GetFieldValue<Guid[]>(10));
}

public sealed record ScheduleResource(Guid Id,string Name,string ResourceType,bool IsActive,string? Notes,DateTimeOffset CreatedAtUtc);
public sealed record ScheduleResourceRequest(string Name,string? ResourceType,bool IsActive=true,string? Notes=null);
public sealed record ScheduleEntry(Guid Id,Guid? JobId,Guid? BookingId,string Title,DateTimeOffset StartsAtUtc,DateTimeOffset EndsAtUtc,string? Location,string Status,string? Notes,DateTimeOffset CreatedAtUtc,IReadOnlyList<Guid> ResourceIds);
public sealed record ScheduleEntryRequest(Guid? JobId,Guid? BookingId,string Title,DateTimeOffset StartsAtUtc,DateTimeOffset EndsAtUtc,string? Location,string? Status,string? Notes,IReadOnlyList<Guid>? ResourceIds,bool AllowConflicts=false);
public sealed record ScheduleConflict(Guid EntryId,string EntryTitle,Guid ResourceId,string ResourceName,DateTimeOffset StartsAtUtc,DateTimeOffset EndsAtUtc);
public sealed class ScheduleConflictException : Exception { public ScheduleConflictException(IReadOnlyList<ScheduleConflict> conflicts) : base("Schedule conflicts were detected.") { Conflicts = conflicts; } public IReadOnlyList<ScheduleConflict> Conflicts { get; } }


public sealed record StaffCapacitySnapshot(
    string UserName,
    string DisplayName,
    Guid ScheduleResourceId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    double EffectiveCapacityMinutes,
    double ScheduledMinutes,
    double UnavailableMinutes,
    double RemainingMinutes,
    double UtilizationPercent,
    string State);

public sealed record ScheduleAssignmentChange(
    Guid Id,
    Guid EntryId,
    Guid? JobId,
    string ChangedBy,
    DateTimeOffset ChangedAtUtc,
    IReadOnlyList<Guid> PreviousResourceIds,
    IReadOnlyList<Guid> NewResourceIds,
    string? Reason);

