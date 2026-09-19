using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Production;

public sealed class PhotographerStore
{
    private readonly StudioDatabase database;

    public PhotographerStore(StudioDatabase database)
    {
        this.database = database;
    }

    public IReadOnlyList<PhotographerProfile> GetAll(bool includeInactive = false)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select user_name,display_name,employment_type,is_active,skills,default_equipment_notes,schedule_resource_id,created_at_utc,updated_at_utc
            from studio_photographers
            where $1 or is_active
            order by display_name,user_name
            """;
        command.Parameters.AddWithValue(includeInactive);
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<PhotographerProfile> values = [];
        while (reader.Read()) values.Add(ReadProfile(reader));
        return values;
    }

    public PhotographerProfile? Get(string userName)
        => GetAll(true).FirstOrDefault(x => string.Equals(x.UserName, userName, StringComparison.OrdinalIgnoreCase));
    public PhotographerProfile? GetByScheduleResourceId(Guid scheduleResourceId)
        => GetAll(true).FirstOrDefault(x => x.ScheduleResourceId == scheduleResourceId);

    public IReadOnlyList<PhotographerProfile> GetByScheduleResourceIds(IEnumerable<Guid> scheduleResourceIds)
    {
        HashSet<Guid> ids = scheduleResourceIds.ToHashSet();
        return GetAll(true).Where(x => ids.Contains(x.ScheduleResourceId)).ToArray();
    }


    public PhotographerProfile Save(PhotographerProfileRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.UserName)) throw new InvalidOperationException("Photographer user name is required.");
        string userName = request.UserName.Trim();
        string displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? userName : request.DisplayName.Trim();
        string employmentType = NormalizeEmploymentType(request.EmploymentType);
        string[] skills = (request.Skills ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlTransaction transaction = connection.BeginTransaction();
        Guid scheduleResourceId = request.ScheduleResourceId ?? EnsureStaffResource(connection, transaction, userName, displayName);
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into studio_photographers(user_name,display_name,employment_type,is_active,skills,default_equipment_notes,schedule_resource_id,created_at_utc,updated_at_utc)
            values($1,$2,$3,$4,$5,$6,$7,now(),now())
            on conflict(user_name) do update set display_name=excluded.display_name,employment_type=excluded.employment_type,is_active=excluded.is_active,
              skills=excluded.skills,default_equipment_notes=excluded.default_equipment_notes,schedule_resource_id=excluded.schedule_resource_id,updated_at_utc=now()
            """;
        command.Parameters.AddWithValue(userName); command.Parameters.AddWithValue(displayName); command.Parameters.AddWithValue(employmentType); command.Parameters.AddWithValue(request.IsActive);
        command.Parameters.AddWithValue(skills); command.Parameters.AddWithValue((object?)Clean(request.DefaultEquipmentNotes) ?? DBNull.Value); command.Parameters.AddWithValue(scheduleResourceId);
        command.ExecuteNonQuery();
        transaction.Commit();
        return Get(userName)!;
    }

    public IReadOnlyList<PhotographerAvailability> GetAvailability(string userName, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select id,user_name,starts_at_utc,ends_at_utc,availability_type,note,created_at_utc
            from studio_photographer_availability
            where lower(user_name)=lower($1) and ($2::timestamptz is null or ends_at_utc>$2) and ($3::timestamptz is null or starts_at_utc<$3)
            order by starts_at_utc
            """;
        command.Parameters.AddWithValue(userName); command.Parameters.AddWithValue((object?)from?.ToUniversalTime() ?? DBNull.Value); command.Parameters.AddWithValue((object?)to?.ToUniversalTime() ?? DBNull.Value);
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<PhotographerAvailability> values = [];
        while (reader.Read()) values.Add(new(reader.GetGuid(0),reader.GetString(1),reader.GetFieldValue<DateTimeOffset>(2),reader.GetFieldValue<DateTimeOffset>(3),reader.GetString(4),reader.IsDBNull(5)?null:reader.GetString(5),reader.GetFieldValue<DateTimeOffset>(6)));
        return values;
    }

    public PhotographerAvailability AddAvailability(string userName, PhotographerAvailabilityRequest request)
    {
        if (request.EndsAtUtc <= request.StartsAtUtc) throw new InvalidOperationException("Availability end must be after start.");
        if (Get(userName) is null) throw new KeyNotFoundException("Photographer was not found.");
        PhotographerAvailability value = new(Guid.NewGuid(),userName.Trim(),request.StartsAtUtc.ToUniversalTime(),request.EndsAtUtc.ToUniversalTime(),NormalizeAvailabilityType(request.AvailabilityType),Clean(request.Note),DateTimeOffset.UtcNow);
        using NpgsqlConnection connection = database.OpenConnection(); using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "insert into studio_photographer_availability(id,user_name,starts_at_utc,ends_at_utc,availability_type,note,created_at_utc) values($1,$2,$3,$4,$5,$6,$7)";
        command.Parameters.AddWithValue(value.Id); command.Parameters.AddWithValue(value.UserName); command.Parameters.AddWithValue(value.StartsAtUtc); command.Parameters.AddWithValue(value.EndsAtUtc); command.Parameters.AddWithValue(value.AvailabilityType); command.Parameters.AddWithValue((object?)value.Note??DBNull.Value); command.Parameters.AddWithValue(value.CreatedAtUtc); command.ExecuteNonQuery();
        return value;
    }

    public bool IsAvailable(string userName, DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc)
    {
        PhotographerProfile? profile = Get(userName);
        if (profile is null || !profile.IsActive) return false;
        using NpgsqlConnection connection = database.OpenConnection(); using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select count(*) from studio_photographer_availability where lower(user_name)=lower($1) and starts_at_utc<$3 and ends_at_utc>$2 and availability_type in ('Unavailable','Leave')";
        command.Parameters.AddWithValue(userName); command.Parameters.AddWithValue(startsAtUtc.ToUniversalTime()); command.Parameters.AddWithValue(endsAtUtc.ToUniversalTime());
        return Convert.ToInt64(command.ExecuteScalar()) == 0;
    }

    private static Guid EnsureStaffResource(NpgsqlConnection connection, NpgsqlTransaction transaction, string userName, string displayName)
    {
        using NpgsqlCommand find = connection.CreateCommand(); find.Transaction = transaction;
        find.CommandText = "select id from studio_schedule_resources where resource_type='Staff' and lower(notes)=lower($1) limit 1";
        find.Parameters.AddWithValue("user:" + userName);
        if (find.ExecuteScalar() is Guid existing) return existing;
        Guid id = Guid.NewGuid();
        using NpgsqlCommand insert = connection.CreateCommand(); insert.Transaction = transaction;
        insert.CommandText = "insert into studio_schedule_resources(id,name,resource_type,is_active,notes,created_at_utc) values($1,$2,'Staff',true,$3,now())";
        insert.Parameters.AddWithValue(id); insert.Parameters.AddWithValue(displayName); insert.Parameters.AddWithValue("user:" + userName); insert.ExecuteNonQuery();
        return id;
    }

    private static PhotographerProfile ReadProfile(NpgsqlDataReader r) => new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetBoolean(3),r.GetFieldValue<string[]>(4),r.IsDBNull(5)?null:r.GetString(5),r.GetGuid(6),r.GetFieldValue<DateTimeOffset>(7),r.GetFieldValue<DateTimeOffset>(8));
    private static string NormalizeEmploymentType(string? value) => value?.Trim() switch { "Freelance"=>"Freelance","Contractor"=>"Contractor","PartTime"=>"PartTime",_=>"Employee" };
    private static string NormalizeAvailabilityType(string? value) => value?.Trim() switch { "Leave"=>"Leave","Preferred"=>"Preferred","Available"=>"Available",_=>"Unavailable" };
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record PhotographerProfile(string UserName,string DisplayName,string EmploymentType,bool IsActive,IReadOnlyList<string> Skills,string? DefaultEquipmentNotes,Guid ScheduleResourceId,DateTimeOffset CreatedAtUtc,DateTimeOffset UpdatedAtUtc);
public sealed record PhotographerProfileRequest(string UserName,string? DisplayName,string? EmploymentType,bool IsActive,IReadOnlyList<string>? Skills,string? DefaultEquipmentNotes,Guid? ScheduleResourceId=null);
public sealed record PhotographerAvailability(Guid Id,string UserName,DateTimeOffset StartsAtUtc,DateTimeOffset EndsAtUtc,string AvailabilityType,string? Note,DateTimeOffset CreatedAtUtc);
public sealed record PhotographerAvailabilityRequest(DateTimeOffset StartsAtUtc,DateTimeOffset EndsAtUtc,string? AvailabilityType,string? Note);
