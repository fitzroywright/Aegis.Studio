using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Scheduling;

public sealed class ScheduleStore
{
    private readonly StudioDatabase database;

    public ScheduleStore(StudioDatabase database)
    {
        this.database = database;
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

    public IReadOnlyList<ScheduleEntry> GetEntries(DateTimeOffset? from = null, DateTimeOffset? to = null)
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
            where e.starts_at_utc < $2 and e.ends_at_utc > $1
            group by e.id
            order by e.starts_at_utc
            """;
        command.Parameters.AddWithValue(start); command.Parameters.AddWithValue(end);
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<ScheduleEntry> values = [];
        while (reader.Read()) values.Add(ReadEntry(reader));
        return values;
    }

    public ScheduleEntry Create(ScheduleEntryRequest request)
    {
        ValidateEntry(request);
        IReadOnlyList<ScheduleConflict> conflicts = FindConflicts(request.StartsAtUtc, request.EndsAtUtc, request.ResourceIds, null);
        if (conflicts.Count > 0 && !request.AllowConflicts)
        {
            throw new ScheduleConflictException(conflicts);
        }

        Guid id = Guid.NewGuid();
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlTransaction transaction = connection.BeginTransaction();
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
        IReadOnlyList<ScheduleConflict> conflicts = FindConflicts(request.StartsAtUtc, request.EndsAtUtc, request.ResourceIds, id);
        if (conflicts.Count > 0 && !request.AllowConflicts) throw new ScheduleConflictException(conflicts);
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlTransaction transaction = connection.BeginTransaction();
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
