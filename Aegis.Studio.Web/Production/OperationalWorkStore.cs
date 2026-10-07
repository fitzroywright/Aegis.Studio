using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Production;

public sealed class OperationalWorkStore
{
    private readonly StudioDatabase database;

    public OperationalWorkStore(StudioDatabase database)
    {
        this.database = database;
    }

    public IReadOnlyList<JobActivity> GetActivities(Guid? jobId = null, string? status = null, string? assignedTo = null)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select id,job_id,activity_type,title,description,status,priority,assigned_to,scheduled_at_utc,due_at_utc,started_at_utc,completed_at_utc,location,blocked_reason,handoff_to,created_by,created_at_utc,updated_at_utc,collection_name,expected_effort_minutes,task_template_id,recipe_step_id,sequence_order,requires_scheduling,assignment_warning
            from studio_job_activities
            where ($1::uuid is null or job_id=$1)
              and ($2::text is null or status=$2)
              and ($3::text is null or lower(assigned_to)=lower($3))
            order by case when due_at_utc is null then 1 else 0 end,due_at_utc,created_at_utc
            """;
        command.Parameters.AddWithValue((object?)jobId ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)Clean(status) ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)Clean(assignedTo) ?? DBNull.Value);
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<JobActivity> values = [];
        while (reader.Read()) values.Add(ReadActivity(reader));
        return values;
    }

    public JobActivity CreateActivity(Guid jobId, JobActivityRequest request, string userName)
    {
        if (string.IsNullOrWhiteSpace(request.Title)) throw new InvalidOperationException("Activity title is required.");
        JobActivity activity = new(
            Guid.NewGuid(), jobId, NormalizeActivityType(request.ActivityType), request.Title.Trim(), Clean(request.Description),
            NormalizeStatus(request.Status), NormalizePriority(request.Priority), Clean(request.AssignedTo), request.ScheduledAtUtc?.ToUniversalTime(),
            request.DueAtUtc?.ToUniversalTime(), null, null, Clean(request.Location), Clean(request.BlockedReason), Clean(request.HandoffTo), userName, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, request.ExpectedEffortMinutes, request.TaskTemplateId, request.RecipeStepId, request.SequenceOrder, request.RequiresScheduling, Clean(request.AssignmentWarning));
        SaveActivity(activity);
        Log(jobId, activity.Id, "Created", userName, $"{activity.ActivityType}: {activity.Title}");
        return activity;
    }

    public JobActivity UpdateActivity(Guid id, JobActivityRequest request, string userName)
    {
        JobActivity current = GetActivity(id) ?? throw new KeyNotFoundException("Activity was not found.");
        string status = NormalizeStatus(request.Status);
        if(status=="Completed")
        {
            using var c=database.OpenConnection();using var cmd=new NpgsqlCommand("select exists(select 1 from studio_production_items where activity_id=$1 and qc_state not in ('Passed','Cancelled'))",c);cmd.Parameters.AddWithValue(id);
            if((bool)cmd.ExecuteScalar()!)throw new InvalidOperationException("Pass quality control on all production items before completing this activity.");
        }
        DateTimeOffset? startedAt = current.StartedAtUtc;
        DateTimeOffset? completedAt = current.CompletedAtUtc;
        if (status == "InProgress" && startedAt is null) startedAt = DateTimeOffset.UtcNow;
        if (status == "Completed" && completedAt is null) completedAt = DateTimeOffset.UtcNow;
        if (status != "Completed") completedAt = null;
        JobActivity updated = current with
        {
            ActivityType = NormalizeActivityType(request.ActivityType), Title = request.Title.Trim(), Description = Clean(request.Description),
            Status = status, Priority = NormalizePriority(request.Priority), AssignedTo = Clean(request.AssignedTo), ScheduledAtUtc = request.ScheduledAtUtc?.ToUniversalTime(),
            DueAtUtc = request.DueAtUtc?.ToUniversalTime(), StartedAtUtc = startedAt, CompletedAtUtc = completedAt,
            Location = Clean(request.Location), BlockedReason = status == "Blocked" ? Clean(request.BlockedReason) : null,
            HandoffTo = Clean(request.HandoffTo), ExpectedEffortMinutes=request.ExpectedEffortMinutes??current.ExpectedEffortMinutes, TaskTemplateId=request.TaskTemplateId??current.TaskTemplateId, RecipeStepId=request.RecipeStepId??current.RecipeStepId, SequenceOrder=request.SequenceOrder??current.SequenceOrder, RequiresScheduling=request.RequiresScheduling??current.RequiresScheduling, AssignmentWarning=request.AssignmentWarning is null?current.AssignmentWarning:Clean(request.AssignmentWarning), UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        SaveActivity(updated);
        Log(updated.JobId, updated.Id, status, userName, request.BlockedReason ?? request.Description);
        return updated;
    }

    public IReadOnlyList<ShootSession> GetShoots(Guid? jobId = null)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select id,job_id,schedule_entry_id,photographer_user_name,assistant_user_name,call_time_utc,planned_end_utc,location,status,checked_in_at_utc,checked_out_at_utc,image_count,backup_confirmed,reshoot_required,coverage_notes,equipment_notes,handoff_status,handoff_to,created_at_utc,updated_at_utc
            from studio_shoot_sessions where ($1::uuid is null or job_id=$1) order by call_time_utc
            """;
        command.Parameters.AddWithValue((object?)jobId ?? DBNull.Value);
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<ShootSession> values = [];
        while (reader.Read()) values.Add(ReadShoot(reader));
        return values;
    }

    public ShootSession CreateShoot(Guid jobId, ShootSessionRequest request, string userName)
    {
        if (string.IsNullOrWhiteSpace(request.PhotographerUserName)) throw new InvalidOperationException("Photographer is required.");
        if (request.PlannedEndUtc <= request.CallTimeUtc) throw new InvalidOperationException("Shoot end must be after call time.");
        ShootSession session = new(Guid.NewGuid(), jobId, request.ScheduleEntryId, request.PhotographerUserName.Trim(), Clean(request.AssistantUserName), request.CallTimeUtc.ToUniversalTime(), request.PlannedEndUtc.ToUniversalTime(), Clean(request.Location), "Scheduled", null, null, 0, false, false, Clean(request.CoverageNotes), Clean(request.EquipmentNotes), "NotReady", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        SaveShoot(session);
        Log(jobId, null, "ShootScheduled", userName, $"Photographer {session.PhotographerUserName} at {session.CallTimeUtc:u}");
        return session;
    }

    public ShootSession UpdateShoot(Guid id, ShootSessionUpdateRequest request, string userName)
    {
        using var connection=database.OpenConnection();using var transaction=connection.BeginTransaction();
        using(var locked=new NpgsqlCommand("select id from studio_shoot_sessions where id=$1 for update",connection,transaction))
        {locked.Parameters.AddWithValue(id);if(locked.ExecuteScalar() is null)throw new KeyNotFoundException("Shoot session was not found.");}
        ShootSession current = GetShoot(id) ?? throw new KeyNotFoundException("Shoot session was not found.");
        string action = request.Action?.Trim().ToLowerInvariant() ?? "update";
        if(action=="checkout" && current.CheckedOutAtUtc.HasValue)return current;
        if(action=="checkin" && current.CheckedInAtUtc.HasValue)return current;
        if(action=="handoff" && current.HandoffStatus=="HandedOff")return current;
        if(current.Status=="Cancelled")throw new InvalidOperationException("A cancelled shoot cannot be updated.");
        if(action=="checkout"&&!current.CheckedInAtUtc.HasValue)throw new InvalidOperationException("Check in before completing the shoot.");
        ShootSession updated = current with { UpdatedAtUtc = DateTimeOffset.UtcNow };
        if (action == "checkin") updated = updated with { Status = "InProgress", CheckedInAtUtc = current.CheckedInAtUtc ?? DateTimeOffset.UtcNow };
        else if (action == "checkout") updated = updated with { Status = "ShootComplete", CheckedOutAtUtc = current.CheckedOutAtUtc ?? DateTimeOffset.UtcNow };
        else if (action == "handoff") updated = updated with { HandoffStatus = "HandedOff", HandoffTo = Clean(request.HandoffTo), Status = "HandedOff" };
        updated = updated with
        {
            ImageCount = request.ImageCount ?? updated.ImageCount,
            BackupConfirmed = request.BackupConfirmed ?? updated.BackupConfirmed,
            ReshootRequired = request.ReshootRequired ?? updated.ReshootRequired,
            CoverageNotes = request.CoverageNotes is null ? updated.CoverageNotes : Clean(request.CoverageNotes),
            EquipmentNotes = request.EquipmentNotes is null ? updated.EquipmentNotes : Clean(request.EquipmentNotes),
            HandoffTo = request.HandoffTo is null ? updated.HandoffTo : Clean(request.HandoffTo)
        };
        SaveShoot(updated,connection,transaction);
        JobShootLifecycle.Advance(connection,transaction,updated.JobId,userName);
        using(var audit=new NpgsqlCommand("insert into studio_operational_audit(id,job_id,activity_id,action,user_name,detail,created_at_utc) values($1,$2,null,$3,$4,$5,now())",connection,transaction))
        {audit.Parameters.AddWithValue(Guid.NewGuid());audit.Parameters.AddWithValue(updated.JobId);audit.Parameters.AddWithValue("Shoot:"+action);audit.Parameters.AddWithValue(userName);audit.Parameters.AddWithValue((object?)request.CoverageNotes??request.EquipmentNotes??(object)DBNull.Value);audit.ExecuteNonQuery();}
        transaction.Commit();
        return updated;
    }

    public IReadOnlyList<OperationalAudit> GetAudit(Guid jobId)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id,job_id,activity_id,action,user_name,detail,created_at_utc from studio_operational_audit where job_id=$1 order by created_at_utc desc limit 200";
        command.Parameters.AddWithValue(jobId);
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<OperationalAudit> values = [];
        while (reader.Read()) values.Add(new(reader.GetGuid(0),reader.GetGuid(1),reader.IsDBNull(2)?null:reader.GetGuid(2),reader.GetString(3),reader.GetString(4),reader.IsDBNull(5)?null:reader.GetString(5),reader.GetFieldValue<DateTimeOffset>(6)));
        return values;
    }

    public OperationalSummary GetSummary()
    {
        IReadOnlyList<JobActivity> activities = GetActivities();
        IReadOnlyList<ShootSession> shoots = GetShoots();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new OperationalSummary(
            shoots.Count(x => x.CallTimeUtc.Date == now.Date && x.Status != "Cancelled"),
            shoots.Count(x => x.Status == "InProgress"),
            shoots.Count(x => x.Status == "ShootComplete" && !x.BackupConfirmed),
            shoots.Count(x => x.ReshootRequired),
            activities.Count(x => x.Status != "Completed"),
            activities.Count(x => x.Status == "Blocked"),
            activities.Count(x => x.Status != "Completed" && x.DueAtUtc.HasValue && x.DueAtUtc.Value < now));
    }

    private JobActivity? GetActivity(Guid id)
    {
        using NpgsqlConnection connection=database.OpenConnection(); using NpgsqlCommand command=connection.CreateCommand();
        command.CommandText="select id,job_id,activity_type,title,description,status,priority,assigned_to,scheduled_at_utc,due_at_utc,started_at_utc,completed_at_utc,location,blocked_reason,handoff_to,created_by,created_at_utc,updated_at_utc,collection_name,expected_effort_minutes,task_template_id,recipe_step_id,sequence_order,requires_scheduling,assignment_warning from studio_job_activities where id=$1";
        command.Parameters.AddWithValue(id); using NpgsqlDataReader reader=command.ExecuteReader(); return reader.Read()?ReadActivity(reader):null;
    }

    private ShootSession? GetShoot(Guid id)
    {
        using NpgsqlConnection connection=database.OpenConnection(); using NpgsqlCommand command=connection.CreateCommand();
        command.CommandText="select id,job_id,schedule_entry_id,photographer_user_name,assistant_user_name,call_time_utc,planned_end_utc,location,status,checked_in_at_utc,checked_out_at_utc,image_count,backup_confirmed,reshoot_required,coverage_notes,equipment_notes,handoff_status,handoff_to,created_at_utc,updated_at_utc from studio_shoot_sessions where id=$1";
        command.Parameters.AddWithValue(id); using NpgsqlDataReader reader=command.ExecuteReader(); return reader.Read()?ReadShoot(reader):null;
    }

    private void SaveActivity(JobActivity a)
    {
        using NpgsqlConnection connection=database.OpenConnection(); using NpgsqlCommand command=connection.CreateCommand();
        command.CommandText="""
            insert into studio_job_activities(id,job_id,activity_type,title,description,status,priority,assigned_to,scheduled_at_utc,due_at_utc,started_at_utc,completed_at_utc,location,blocked_reason,handoff_to,created_by,created_at_utc,updated_at_utc,expected_effort_minutes,task_template_id,recipe_step_id,sequence_order,requires_scheduling,assignment_warning)
            values($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21,$22,$23,$24)
            on conflict(id) do update set activity_type=excluded.activity_type,title=excluded.title,description=excluded.description,status=excluded.status,priority=excluded.priority,assigned_to=excluded.assigned_to,scheduled_at_utc=excluded.scheduled_at_utc,due_at_utc=excluded.due_at_utc,started_at_utc=excluded.started_at_utc,completed_at_utc=excluded.completed_at_utc,location=excluded.location,blocked_reason=excluded.blocked_reason,handoff_to=excluded.handoff_to,expected_effort_minutes=excluded.expected_effort_minutes,task_template_id=excluded.task_template_id,recipe_step_id=excluded.recipe_step_id,sequence_order=excluded.sequence_order,requires_scheduling=excluded.requires_scheduling,assignment_warning=excluded.assignment_warning,updated_at_utc=excluded.updated_at_utc
            """;
        object?[] values=[a.Id,a.JobId,a.ActivityType,a.Title,(object?)a.Description??DBNull.Value,a.Status,a.Priority,(object?)a.AssignedTo??DBNull.Value,(object?)a.ScheduledAtUtc??DBNull.Value,(object?)a.DueAtUtc??DBNull.Value,(object?)a.StartedAtUtc??DBNull.Value,(object?)a.CompletedAtUtc??DBNull.Value,(object?)a.Location??DBNull.Value,(object?)a.BlockedReason??DBNull.Value,(object?)a.HandoffTo??DBNull.Value,a.CreatedBy,a.CreatedAtUtc,a.UpdatedAtUtc,(object?)a.ExpectedEffortMinutes??DBNull.Value,(object?)a.TaskTemplateId??DBNull.Value,(object?)a.RecipeStepId??DBNull.Value,(object?)a.SequenceOrder??DBNull.Value,a.RequiresScheduling,(object?)a.AssignmentWarning??DBNull.Value];
        foreach(object? value in values) command.Parameters.AddWithValue(value??DBNull.Value); command.ExecuteNonQuery();
    }

    private void SaveShoot(ShootSession s)
    {
        using var connection=database.OpenConnection();using var transaction=connection.BeginTransaction();
        SaveShoot(s,connection,transaction);transaction.Commit();
    }
    private static void SaveShoot(ShootSession s,NpgsqlConnection connection,NpgsqlTransaction transaction)
    {
        using NpgsqlCommand command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="""
            insert into studio_shoot_sessions(id,job_id,schedule_entry_id,photographer_user_name,assistant_user_name,call_time_utc,planned_end_utc,location,status,checked_in_at_utc,checked_out_at_utc,image_count,backup_confirmed,reshoot_required,coverage_notes,equipment_notes,handoff_status,handoff_to,created_at_utc,updated_at_utc)
            values($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20)
            on conflict(id) do update set photographer_user_name=excluded.photographer_user_name,assistant_user_name=excluded.assistant_user_name,call_time_utc=excluded.call_time_utc,planned_end_utc=excluded.planned_end_utc,location=excluded.location,status=excluded.status,checked_in_at_utc=excluded.checked_in_at_utc,checked_out_at_utc=excluded.checked_out_at_utc,image_count=excluded.image_count,backup_confirmed=excluded.backup_confirmed,reshoot_required=excluded.reshoot_required,coverage_notes=excluded.coverage_notes,equipment_notes=excluded.equipment_notes,handoff_status=excluded.handoff_status,handoff_to=excluded.handoff_to,updated_at_utc=excluded.updated_at_utc
            """;
        object?[] values=[s.Id,s.JobId,(object?)s.ScheduleEntryId??DBNull.Value,s.PhotographerUserName,(object?)s.AssistantUserName??DBNull.Value,s.CallTimeUtc,s.PlannedEndUtc,(object?)s.Location??DBNull.Value,s.Status,(object?)s.CheckedInAtUtc??DBNull.Value,(object?)s.CheckedOutAtUtc??DBNull.Value,s.ImageCount,s.BackupConfirmed,s.ReshootRequired,(object?)s.CoverageNotes??DBNull.Value,(object?)s.EquipmentNotes??DBNull.Value,s.HandoffStatus,(object?)s.HandoffTo??DBNull.Value,s.CreatedAtUtc,s.UpdatedAtUtc];
        foreach(object? value in values) command.Parameters.AddWithValue(value??DBNull.Value); command.ExecuteNonQuery();
    }

    private void Log(Guid jobId, Guid? activityId, string action, string userName, string? detail)
    {
        using NpgsqlConnection connection=database.OpenConnection(); using NpgsqlCommand command=connection.CreateCommand();
        command.CommandText="insert into studio_operational_audit(id,job_id,activity_id,action,user_name,detail,created_at_utc) values($1,$2,$3,$4,$5,$6,now())";
        command.Parameters.AddWithValue(Guid.NewGuid()); command.Parameters.AddWithValue(jobId); command.Parameters.AddWithValue((object?)activityId??DBNull.Value); command.Parameters.AddWithValue(action); command.Parameters.AddWithValue(userName); command.Parameters.AddWithValue((object?)Clean(detail)??DBNull.Value); command.ExecuteNonQuery();
    }

    private static JobActivity ReadActivity(NpgsqlDataReader r) => new(r.GetGuid(0),r.GetGuid(1),r.GetString(2),r.GetString(3),r.IsDBNull(4)?null:r.GetString(4),r.GetString(5),r.GetString(6),r.IsDBNull(7)?null:r.GetString(7),r.IsDBNull(8)?null:r.GetFieldValue<DateTimeOffset>(8),r.IsDBNull(9)?null:r.GetFieldValue<DateTimeOffset>(9),r.IsDBNull(10)?null:r.GetFieldValue<DateTimeOffset>(10),r.IsDBNull(11)?null:r.GetFieldValue<DateTimeOffset>(11),r.IsDBNull(12)?null:r.GetString(12),r.IsDBNull(13)?null:r.GetString(13),r.IsDBNull(14)?null:r.GetString(14),r.GetString(15),r.GetFieldValue<DateTimeOffset>(16),r.GetFieldValue<DateTimeOffset>(17),r.IsDBNull(18)?null:r.GetString(18),r.IsDBNull(19)?null:r.GetInt32(19),r.IsDBNull(20)?null:r.GetGuid(20),r.IsDBNull(21)?null:r.GetGuid(21),r.IsDBNull(22)?null:r.GetInt32(22),!r.IsDBNull(23)&&r.GetBoolean(23),r.IsDBNull(24)?null:r.GetString(24));
    private static ShootSession ReadShoot(NpgsqlDataReader r) => new(r.GetGuid(0),r.GetGuid(1),r.IsDBNull(2)?null:r.GetGuid(2),r.GetString(3),r.IsDBNull(4)?null:r.GetString(4),r.GetFieldValue<DateTimeOffset>(5),r.GetFieldValue<DateTimeOffset>(6),r.IsDBNull(7)?null:r.GetString(7),r.GetString(8),r.IsDBNull(9)?null:r.GetFieldValue<DateTimeOffset>(9),r.IsDBNull(10)?null:r.GetFieldValue<DateTimeOffset>(10),r.GetInt32(11),r.GetBoolean(12),r.GetBoolean(13),r.IsDBNull(14)?null:r.GetString(14),r.IsDBNull(15)?null:r.GetString(15),r.GetString(16),r.IsDBNull(17)?null:r.GetString(17),r.GetFieldValue<DateTimeOffset>(18),r.GetFieldValue<DateTimeOffset>(19));
    private static string NormalizeActivityType(string? value) => value?.Trim() switch { "Shoot"=>"Shoot","Travel"=>"Travel","EquipmentPrep"=>"EquipmentPrep","FileIngest"=>"FileIngest","Backup"=>"Backup","Cull"=>"Cull","Edit"=>"Edit","Retouch"=>"Retouch","Proof"=>"Proof","Print"=>"Print","QC"=>"QC","Delivery"=>"Delivery","Archive"=>"Archive","Reshoot"=>"Reshoot",_=>"Other" };
    private static string NormalizeStatus(string? value) => value?.Trim() switch { "Scheduled"=>"Scheduled","InProgress"=>"InProgress","Blocked"=>"Blocked","Completed"=>"Completed","Cancelled"=>"Cancelled","WaitingOnCustomer"=>"WaitingOnCustomer","ReworkRequired"=>"ReworkRequired","Partial"=>"Partial","Failed"=>"Failed","ReprintRequired"=>"ReprintRequired","ReadyForDelivery"=>"ReadyForDelivery","Archived"=>"Archived",_=>"NotStarted" };
    private static string NormalizePriority(string? value) => value?.Trim() switch { "Low"=>"Low","High"=>"High","Urgent"=>"Urgent",_=>"Normal" };
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value)?null:value.Trim();
}

public sealed record JobActivity(Guid Id,Guid JobId,string ActivityType,string Title,string? Description,string Status,string Priority,string? AssignedTo,DateTimeOffset? ScheduledAtUtc,DateTimeOffset? DueAtUtc,DateTimeOffset? StartedAtUtc,DateTimeOffset? CompletedAtUtc,string? Location,string? BlockedReason,string? HandoffTo,string CreatedBy,DateTimeOffset CreatedAtUtc,DateTimeOffset UpdatedAtUtc,string? CollectionName=null,int? ExpectedEffortMinutes=null,Guid? TaskTemplateId=null,Guid? RecipeStepId=null,int? SequenceOrder=null,bool RequiresScheduling=false,string? AssignmentWarning=null);
public sealed record JobActivityRequest(string? ActivityType,string Title,string? Description,string? Status,string? Priority,string? AssignedTo,DateTimeOffset? ScheduledAtUtc,DateTimeOffset? DueAtUtc,string? Location,string? BlockedReason,string? HandoffTo,int? ExpectedEffortMinutes=null,Guid? TaskTemplateId=null,Guid? RecipeStepId=null,int? SequenceOrder=null,bool? RequiresScheduling=null,string? AssignmentWarning=null);
public sealed record ShootSession(Guid Id,Guid JobId,Guid? ScheduleEntryId,string PhotographerUserName,string? AssistantUserName,DateTimeOffset CallTimeUtc,DateTimeOffset PlannedEndUtc,string? Location,string Status,DateTimeOffset? CheckedInAtUtc,DateTimeOffset? CheckedOutAtUtc,int ImageCount,bool BackupConfirmed,bool ReshootRequired,string? CoverageNotes,string? EquipmentNotes,string HandoffStatus,string? HandoffTo,DateTimeOffset CreatedAtUtc,DateTimeOffset UpdatedAtUtc);
public sealed record ShootSessionRequest(Guid? ScheduleEntryId,string PhotographerUserName,string? AssistantUserName,DateTimeOffset CallTimeUtc,DateTimeOffset PlannedEndUtc,string? Location,string? CoverageNotes,string? EquipmentNotes);
public sealed record ShootSessionUpdateRequest(string? Action,int? ImageCount,bool? BackupConfirmed,bool? ReshootRequired,string? CoverageNotes,string? EquipmentNotes,string? HandoffTo);
public sealed record OperationalAudit(Guid Id,Guid JobId,Guid? ActivityId,string Action,string UserName,string? Detail,DateTimeOffset CreatedAtUtc);
public sealed record OperationalSummary(int ShootsToday,int ShootsInProgress,int ShootsAwaitingBackup,int ReshootsRequired,int OpenActivities,int BlockedActivities,int OverdueActivities);


