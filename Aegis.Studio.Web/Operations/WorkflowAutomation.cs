using System.Text.Json;
using Aegis.Studio.Web.Email;
using Aegis.Studio.Web.Notifications;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Production;
using Aegis.Studio.Web.Scheduling;
using Npgsql;

namespace Aegis.Studio.Web.Operations;

public static class WorkflowAutomationRuntime
{
    private static WorkflowAutomationEngine? engine;

    public static void Initialize(WorkflowAutomationEngine value)
    {
        engine = value;
    }

    public static void Publish(string eventType, Guid jobId, object? context = null)
    {
        engine?.HandleEvent(eventType, jobId, context);
    }
}

public static class WorkflowAutomationEndpoints
{
    public static void MapStudioWorkflowAutomation(this WebApplication app)
    {
        StudioDatabase database = app.Services.GetRequiredService<StudioDatabase>();
        WorkflowAutomationEngine engine = new(
            database,
            app.Services.GetRequiredService<StudioStore>(),
            app.Services.GetRequiredService<NotificationService>(),
            app.Services.GetRequiredService<WorkspaceAccessService>(),
            app.Services.GetRequiredService<JobAccessService>(),
            app.Services.GetRequiredService<OutboundEmailStore>(),
            app.Services.GetRequiredService<IConfiguration>());
        engine.EnsureSchema();
        WorkflowAutomationRuntime.Initialize(engine);

        RouteGroupBuilder group = app.MapGroup("/api/workflows").RequireAuthorization();
        group.MapGet("/", (HttpContext context) => context.User.IsInRole("Administrator") ? Results.Ok(engine.GetDefinitions()) : Results.Forbid());
        group.MapPost("/", (WorkflowDefinitionRequest request, HttpContext context) =>
        {
            if (!context.User.IsInRole("Administrator")) return Results.Forbid();
            try { return Results.Ok(engine.SaveDefinition(null, request)); }
            catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
        });
        group.MapPut("/{id:guid}", (Guid id, WorkflowDefinitionRequest request, HttpContext context) =>
        {
            if (!context.User.IsInRole("Administrator")) return Results.Forbid();
            try { return Results.Ok(engine.SaveDefinition(id, request)); }
            catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
        });
        group.MapDelete("/{id:guid}", (Guid id, HttpContext context) =>
        {
            if (!context.User.IsInRole("Administrator")) return Results.Forbid();
            engine.DeleteDefinition(id);
            return Results.NoContent();
        });
        group.MapGet("/runs", (HttpContext context, int? count) => context.User.IsInRole("Administrator") ? Results.Ok(engine.GetRuns(count ?? 200)) : Results.Forbid());
        group.MapGet("/{id:guid}/preview/{jobId:guid}", (Guid id, Guid jobId, HttpContext context) =>
        {
            if (!context.User.IsInRole("Administrator")) return Results.Forbid();
            try { return Results.Ok(engine.PreviewDefinition(id, jobId)); }
            catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
        });
        group.MapPost("/{id:guid}/test/{jobId:guid}", (Guid id, Guid jobId, HttpContext context) =>
        {
            if (!context.User.IsInRole("Administrator")) return Results.Forbid();
            try { return Results.Ok(engine.RunDefinition(id, jobId, "ManualTest", new { manual = true })); }
            catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
        });

        CancellationToken token = app.Lifetime.ApplicationStopping;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    engine.ResumeDueRuns();
                }
                catch (Exception exception)
                {
                    app.Logger.LogError(exception, "Workflow scheduler cycle failed.");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, token);
    }
}

public sealed class WorkflowAutomationEngine
{
    private const int MaximumAttempts = 5;
    private static readonly HashSet<string> supportedActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "CreateActivity",
        "NotifyJob",
        "NotifyRequester",
        "WaitMinutes"
    };

    private readonly StudioDatabase database;
    private readonly StudioStore studio;
    private readonly NotificationService notifications;
    private readonly WorkspaceAccessService workspaceAccess;
    private readonly JobAccessService jobAccess;
    private readonly OutboundEmailStore outbox;
    private readonly IConfiguration configuration;
    private readonly string instanceId = Guid.NewGuid().ToString("N");

    public WorkflowAutomationEngine(
        StudioDatabase database,
        StudioStore studio,
        NotificationService notifications,
        WorkspaceAccessService workspaceAccess,
        JobAccessService jobAccess,
        OutboundEmailStore outbox,
        IConfiguration configuration)
    {
        this.database = database;
        this.studio = studio;
        this.notifications = notifications;
        this.workspaceAccess = workspaceAccess;
        this.jobAccess = jobAccess;
        this.outbox = outbox;
        this.configuration = configuration;
    }

    public void EnsureSchema()
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            create table if not exists studio_workflow_definitions
            (
                id uuid primary key,
                name text not null unique,
                description text null,
                trigger_event text not null,
                conditions_json jsonb not null default '{}'::jsonb,
                actions_json jsonb not null,
                is_enabled boolean not null default true,
                created_at_utc timestamptz not null,
                updated_at_utc timestamptz not null
            );
            create table if not exists studio_workflow_runs
            (
                id uuid primary key,
                workflow_id uuid not null references studio_workflow_definitions(id) on delete cascade,
                job_id uuid not null references studio_jobs(id) on delete cascade,
                trigger_event text not null,
                status text not null,
                action_index integer not null default 0,
                context_json jsonb not null default '{}'::jsonb,
                next_action_at_utc timestamptz null,
                last_error text null,
                lease_owner text null,
                lease_until_utc timestamptz null,
                attempt_count integer not null default 0,
                created_at_utc timestamptz not null,
                updated_at_utc timestamptz not null,
                completed_at_utc timestamptz null
            );
            alter table studio_workflow_runs add column if not exists lease_owner text null;
            alter table studio_workflow_runs add column if not exists lease_until_utc timestamptz null;
            alter table studio_workflow_runs add column if not exists attempt_count integer not null default 0;
            create index if not exists ix_studio_workflow_runs_due on studio_workflow_runs(status,next_action_at_utc);
            create index if not exists ix_studio_workflow_runs_lease on studio_workflow_runs(status,lease_until_utc);
            create table if not exists studio_workflow_run_log
            (
                id uuid primary key,
                run_id uuid not null references studio_workflow_runs(id) on delete cascade,
                action_index integer not null,
                action_type text not null,
                message text not null,
                created_at_utc timestamptz not null
            );
            create index if not exists ix_studio_workflow_run_log_run on studio_workflow_run_log(run_id,action_index,created_at_utc);
            """;
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<WorkflowDefinition> GetDefinitions()
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id,name,description,trigger_event,conditions_json::text,actions_json::text,is_enabled,created_at_utc,updated_at_utc from studio_workflow_definitions order by name";
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<WorkflowDefinition> values = [];
        while (reader.Read())
        {
            values.Add(new WorkflowDefinition(reader.GetGuid(0),reader.GetString(1),reader.IsDBNull(2)?null:reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetBoolean(6),reader.GetFieldValue<DateTimeOffset>(7),reader.GetFieldValue<DateTimeOffset>(8)));
        }
        return values;
    }

    public WorkflowDefinition SaveDefinition(Guid? id, WorkflowDefinitionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.TriggerEvent)) throw new InvalidOperationException("Workflow name and trigger event are required.");
        ValidateJsonObject(request.ConditionsJson);
        ValidateActions(request.ActionsJson);
        Guid workflowId = id ?? Guid.NewGuid();
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            insert into studio_workflow_definitions(id,name,description,trigger_event,conditions_json,actions_json,is_enabled,created_at_utc,updated_at_utc)
            values($1,$2,$3,$4,$5::jsonb,$6::jsonb,$7,now(),now())
            on conflict(id) do update set name=excluded.name,description=excluded.description,trigger_event=excluded.trigger_event,conditions_json=excluded.conditions_json,actions_json=excluded.actions_json,is_enabled=excluded.is_enabled,updated_at_utc=now()
            """;
        command.Parameters.AddWithValue(workflowId);
        command.Parameters.AddWithValue(request.Name.Trim());
        command.Parameters.AddWithValue((object?)request.Description?.Trim() ?? DBNull.Value);
        command.Parameters.AddWithValue(request.TriggerEvent.Trim());
        command.Parameters.AddWithValue(string.IsNullOrWhiteSpace(request.ConditionsJson) ? "{}" : request.ConditionsJson);
        command.Parameters.AddWithValue(request.ActionsJson);
        command.Parameters.AddWithValue(request.IsEnabled);
        command.ExecuteNonQuery();
        return GetDefinitions().First(value => value.Id == workflowId);
    }

    public void DeleteDefinition(Guid id)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "update studio_workflow_definitions set is_enabled=false,updated_at_utc=now() where id=$1";
        command.Parameters.AddWithValue(id);
        command.ExecuteNonQuery();
    }

    public void HandleEvent(string eventType, Guid jobId, object? context)
    {
        foreach (WorkflowDefinition definition in GetDefinitions().Where(value => value.IsEnabled && value.TriggerEvent.Equals(eventType, StringComparison.OrdinalIgnoreCase)))
        {
            if (Matches(definition, jobId, context)) RunDefinition(definition.Id, jobId, eventType, context);
        }
    }

    public WorkflowPreview PreviewDefinition(Guid definitionId, Guid jobId)
    {
        WorkflowDefinition definition = GetDefinitions().FirstOrDefault(value => value.Id == definitionId) ?? throw new KeyNotFoundException("Workflow definition was not found.");
        StudioJob job = studio.GetJob(jobId) ?? throw new KeyNotFoundException("Job was not found.");
        JsonElement actions = JsonDocument.Parse(definition.ActionsJson).RootElement;
        IReadOnlyList<string> actionTypes = actions.EnumerateArray().Select(value => value.GetProperty("type").GetString() ?? string.Empty).ToList();
        return new WorkflowPreview(definition.Id, definition.Name, job.Id, job.Title, Matches(definition, jobId, null), actionTypes);
    }

    public WorkflowRun RunDefinition(Guid definitionId, Guid jobId, string triggerEvent, object? context)
    {
        WorkflowDefinition definition = GetDefinitions().FirstOrDefault(value => value.Id == definitionId) ?? throw new KeyNotFoundException("Workflow definition was not found.");
        if (!definition.IsEnabled && !triggerEvent.Equals("ManualTest", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Workflow definition is disabled.");
        if (studio.GetJob(jobId) is null) throw new KeyNotFoundException("Job was not found.");

        Guid runId = Guid.NewGuid();
        string contextJson = JsonSerializer.Serialize(context ?? new { });
        using (NpgsqlConnection connection = database.OpenConnection())
        using (NpgsqlCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                insert into studio_workflow_runs(id,workflow_id,job_id,trigger_event,status,action_index,context_json,lease_owner,lease_until_utc,attempt_count,created_at_utc,updated_at_utc)
                values($1,$2,$3,$4,'Running',0,$5::jsonb,$6,now()+interval '2 minutes',1,now(),now())
                """;
            command.Parameters.AddWithValue(runId);
            command.Parameters.AddWithValue(definitionId);
            command.Parameters.AddWithValue(jobId);
            command.Parameters.AddWithValue(triggerEvent);
            command.Parameters.AddWithValue(contextJson);
            command.Parameters.AddWithValue(instanceId);
            command.ExecuteNonQuery();
        }
        Execute(runId);
        return GetRun(runId)!;
    }

    public void ResumeDueRuns()
    {
        FailExhaustedRuns();
        List<Guid> ids = [];
        using (NpgsqlConnection connection = database.OpenConnection())
        using (NpgsqlTransaction transaction = connection.BeginTransaction())
        {
            using NpgsqlCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                with candidates as
                (
                    select id
                    from studio_workflow_runs
                    where attempt_count < $2
                      and (
                           (status='Waiting' and next_action_at_utc<=now())
                           or (status='Running' and lease_until_utc is not null and lease_until_utc<=now())
                      )
                    order by coalesce(next_action_at_utc,lease_until_utc),created_at_utc
                    for update skip locked
                    limit 50
                )
                update studio_workflow_runs r
                set status='Running',lease_owner=$1,lease_until_utc=now()+interval '2 minutes',attempt_count=r.attempt_count+1,updated_at_utc=now()
                from candidates c
                where r.id=c.id
                returning r.id
                """;
            command.Parameters.AddWithValue(instanceId);
            command.Parameters.AddWithValue(MaximumAttempts);
            using NpgsqlDataReader reader = command.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetGuid(0));
            reader.Close();
            transaction.Commit();
        }

        foreach (Guid id in ids) Execute(id);
    }

    public IReadOnlyList<WorkflowRun> GetRuns(int count)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id,workflow_id,job_id,trigger_event,status,action_index,context_json::text,next_action_at_utc,last_error,created_at_utc,updated_at_utc,completed_at_utc from studio_workflow_runs order by created_at_utc desc limit $1";
        command.Parameters.AddWithValue(Math.Clamp(count, 1, 1000));
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<WorkflowRun> values = [];
        while (reader.Read()) values.Add(ReadRun(reader));
        return values;
    }

    private WorkflowRun? GetRun(Guid id)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id,workflow_id,job_id,trigger_event,status,action_index,context_json::text,next_action_at_utc,last_error,created_at_utc,updated_at_utc,completed_at_utc from studio_workflow_runs where id=$1";
        command.Parameters.AddWithValue(id);
        using NpgsqlDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadRun(reader) : null;
    }

    private void Execute(Guid runId)
    {
        WorkflowRun run = GetRun(runId) ?? throw new KeyNotFoundException("Workflow run not found.");
        WorkflowDefinition definition = GetDefinitions().First(value => value.Id == run.WorkflowId);
        JsonElement actions = JsonDocument.Parse(definition.ActionsJson).RootElement;

        for (int index = run.ActionIndex; index < actions.GetArrayLength(); index++)
        {
            RenewLease(runId);
            JsonElement action = actions[index];
            string type = action.GetProperty("type").GetString() ?? string.Empty;
            if (WasActionCompleted(runId, index))
            {
                SetRun(runId, "Running", index + 1, null, null, false);
                continue;
            }

            try
            {
                if (type.Equals("WaitMinutes", StringComparison.OrdinalIgnoreCase))
                {
                    int minutes = action.TryGetProperty("minutes", out JsonElement value) ? Math.Clamp(value.GetInt32(), 1, 525600) : 1;
                    Log(runId, index, type, $"Waiting {minutes} minute(s).");
                    SetRun(runId, "Waiting", index + 1, DateTimeOffset.UtcNow.AddMinutes(minutes), null, false);
                    return;
                }

                ExecuteAction(run.JobId, type, action);
                Log(runId, index, type, "Completed");
                SetRun(runId, "Running", index + 1, null, null, false);
            }
            catch (Exception exception)
            {
                Log(runId, index, type, "Failed: " + exception.Message);
                SetRun(runId, "Failed", index, null, exception.Message, false);
                return;
            }
        }

        SetRun(runId, "Completed", actions.GetArrayLength(), null, null, true);
    }

    private void ExecuteAction(Guid jobId, string type, JsonElement action)
    {
        switch (type.ToLowerInvariant())
        {
            case "createactivity":
                string activityType = Text(action, "activityType", "Other");
                if(ProductionAuthorityRules.IsProductionTask(activityType))throw new InvalidOperationException("Production activities require explicit Production Manager dispatch through Job workflow.");
                string title = Text(action, "title", activityType);
                string? assigned = OptionalText(action, "assignedTo");
                int? dueMinutes = OptionalInt(action, "dueMinutes");
                new OperationalWorkStore(database).CreateActivity(jobId, new JobActivityRequest(activityType,title,assigned,"NotStarted",Text(action,"priority","Normal"),null,dueMinutes.HasValue?DateTimeOffset.UtcNow.AddMinutes(dueMinutes.Value):null,null,null,null,null), "WORKFLOW");
                if (!string.IsNullOrWhiteSpace(assigned)) jobAccess.EnsureAssignedMember(jobId, assigned);
                break;
            case "changejobstatus":case "assignuser":
                throw new InvalidOperationException("Legacy automatic Job status/ownership actions require an authorized connected workflow decision.");
            case "notifyjob":
                PublishJob(jobId,"WorkflowNotification",Text(action,"subject","Workflow notification"),Text(action,"body","Workflow automation generated this notification."));
                break;
            case "notifyrequester":
                NotifyRequester(jobId,Text(action,"subject","Job update"),Text(action,"body","There is an update to your request."),Text(action,"preference","JobStatusChanged"));
                break;
            case "createschedule":
                throw new InvalidOperationException("Scheduling requires an authorized Job workflow decision.");
            default:
                throw new InvalidOperationException($"Unsupported workflow action '{type}'.");
        }
    }

    private void PublishJob(Guid jobId, string eventType, string subject, string body)
    {
        StudioJob? job = studio.GetJob(jobId);
        if (job is null) return;
        HashSet<string> recipients = new(StringComparer.OrdinalIgnoreCase);
        foreach (JobMember member in jobAccess.GetMembers(jobId).Where(value => value.IsActive)) recipients.Add(member.UserName);
        foreach (WorkspaceMember member in workspaceAccess.GetMembers(job.WorkspaceId).Where(value => value.IsActive)) recipients.Add(member.UserName);
        foreach (string user in recipients) notifications.PublishToUser(user,eventType,subject,body,"Job",jobId.ToString("D"));
    }

    private void NotifyRequester(Guid jobId, string subject, string body, string preferenceEvent)
    {
        if (!RequesterAllows(jobId, preferenceEvent)) return;
        StudioJob? job = studio.GetJob(jobId);
        if (job is null) return;
        StudioConversation? conversation = studio.GetConversation(job.ConversationId);
        if (conversation is not null && conversation.Channel.Equals("Email", StringComparison.OrdinalIgnoreCase) && conversation.ContactAddress.Contains('@')) outbox.QueueNotification(conversation, subject, body);
    }

    private bool RequesterAllows(Guid jobId, string eventType)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select schedule_changes,status_changes,production_milestones,delivery_updates from studio_requester_notification_preferences where job_id=$1";
        command.Parameters.AddWithValue(jobId);
        using NpgsqlDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return false;
        return eventType switch
        {
            "ScheduleChanged" => reader.GetBoolean(0),
            "JobStatusChanged" => reader.GetBoolean(1),
            "ProductionChanged" => reader.GetBoolean(2),
            "DeliveryChanged" => reader.GetBoolean(3),
            _ => false
        };
    }

    private bool Matches(WorkflowDefinition definition, Guid jobId, object? context)
    {
        StudioJob? job = studio.GetJob(jobId);
        if (job is null) return false;
        JsonElement conditions = JsonDocument.Parse(definition.ConditionsJson).RootElement;
        if (conditions.ValueKind != JsonValueKind.Object) return true;
        if (conditions.TryGetProperty("workspaceId", out JsonElement workspace) && Guid.TryParse(workspace.GetString(), out Guid workspaceId) && job.WorkspaceId != workspaceId) return false;
        if (conditions.TryGetProperty("jobStatus", out JsonElement status) && !job.Status.Equals(status.GetString(), StringComparison.OrdinalIgnoreCase)) return false;
        if (conditions.TryGetProperty("assignedTo", out JsonElement assigned) && !string.Equals(job.AssignedTo, assigned.GetString(), StringComparison.OrdinalIgnoreCase)) return false;
        if (conditions.TryGetProperty("context", out JsonElement expectedContext) && expectedContext.ValueKind == JsonValueKind.Object && context is not null)
        {
            JsonElement actualContext = JsonSerializer.SerializeToElement(context);
            foreach (JsonProperty property in expectedContext.EnumerateObject())
            {
                if (!actualContext.TryGetProperty(property.Name, out JsonElement actual) || actual.ToString() != property.Value.ToString()) return false;
            }
        }
        return true;
    }

    private void FailExhaustedRuns()
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            update studio_workflow_runs
            set status='Failed',last_error=coalesce(last_error,'Workflow exceeded maximum recovery attempts.'),lease_owner=null,lease_until_utc=null,updated_at_utc=now()
            where status='Running' and lease_until_utc<=now() and attempt_count >= $1
            """;
        command.Parameters.AddWithValue(MaximumAttempts);
        command.ExecuteNonQuery();
    }

    private void RenewLease(Guid runId)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "update studio_workflow_runs set lease_until_utc=now()+interval '2 minutes',updated_at_utc=now() where id=$1 and status='Running' and lease_owner=$2";
        command.Parameters.AddWithValue(runId);
        command.Parameters.AddWithValue(instanceId);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Workflow execution lease was lost.");
    }

    private bool WasActionCompleted(Guid runId, int actionIndex)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select exists(select 1 from studio_workflow_run_log where run_id=$1 and action_index=$2 and message='Completed')";
        command.Parameters.AddWithValue(runId);
        command.Parameters.AddWithValue(actionIndex);
        return command.ExecuteScalar() is bool value && value;
    }

    private void SetRun(Guid id, string status, int actionIndex, DateTimeOffset? next, string? error, bool completed)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            update studio_workflow_runs
            set status=$2,action_index=$3,next_action_at_utc=$4,last_error=$5,updated_at_utc=now(),
                completed_at_utc=case when $6 then now() else completed_at_utc end,
                lease_owner=case when $2='Running' then lease_owner else null end,
                lease_until_utc=case when $2='Running' then lease_until_utc else null end
            where id=$1 and (lease_owner=$7 or lease_owner is null)
            """;
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(status);
        command.Parameters.AddWithValue(actionIndex);
        command.Parameters.AddWithValue((object?)next ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue(completed);
        command.Parameters.AddWithValue(instanceId);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Workflow execution state changed concurrently.");
    }

    private void Log(Guid runId, int index, string type, string message)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "insert into studio_workflow_run_log(id,run_id,action_index,action_type,message,created_at_utc) values($1,$2,$3,$4,$5,now())";
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(runId);
        command.Parameters.AddWithValue(index);
        command.Parameters.AddWithValue(type);
        command.Parameters.AddWithValue(message);
        command.ExecuteNonQuery();
    }

    private static void ValidateJsonObject(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        JsonElement element = JsonDocument.Parse(value).RootElement;
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Conditions must be a JSON object.");
    }

    private static void ValidateActions(string value)
    {
        JsonElement element = JsonDocument.Parse(value).RootElement;
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0) throw new InvalidOperationException("Actions must be a non-empty JSON array.");
        foreach (JsonElement action in element.EnumerateArray())
        {
            if (!action.TryGetProperty("type", out JsonElement typeElement) || typeElement.ValueKind != JsonValueKind.String) throw new InvalidOperationException("Every workflow action requires a string type.");
            string type = typeElement.GetString() ?? string.Empty;
            if (!supportedActions.Contains(type)) throw new InvalidOperationException($"Unsupported workflow action '{type}'.");
            if(type.Equals("CreateActivity",StringComparison.OrdinalIgnoreCase)&&ProductionAuthorityRules.IsProductionTask(OptionalText(action,"activityType")??"Other"))throw new InvalidOperationException("Production activities require explicit Production Manager dispatch through Job workflow.");
            if (type.Equals("AssignUser", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(OptionalText(action,"userName"))) throw new InvalidOperationException("AssignUser requires userName.");
            if (type.Equals("ChangeJobStatus", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(OptionalText(action,"status"))) throw new InvalidOperationException("ChangeJobStatus requires status.");
        }
    }

    private static string Text(JsonElement element, string name, string fallback)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;

    private static string? OptionalText(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? OptionalInt(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int number) ? number : null;

    private static WorkflowRun ReadRun(NpgsqlDataReader reader)
        => new(reader.GetGuid(0),reader.GetGuid(1),reader.GetGuid(2),reader.GetString(3),reader.GetString(4),reader.GetInt32(5),reader.GetString(6),reader.IsDBNull(7)?null:reader.GetFieldValue<DateTimeOffset>(7),reader.IsDBNull(8)?null:reader.GetString(8),reader.GetFieldValue<DateTimeOffset>(9),reader.GetFieldValue<DateTimeOffset>(10),reader.IsDBNull(11)?null:reader.GetFieldValue<DateTimeOffset>(11));
}

public sealed record WorkflowDefinition(Guid Id,string Name,string? Description,string TriggerEvent,string ConditionsJson,string ActionsJson,bool IsEnabled,DateTimeOffset CreatedAtUtc,DateTimeOffset UpdatedAtUtc);
public sealed record WorkflowDefinitionRequest(string Name,string? Description,string TriggerEvent,string ConditionsJson,string ActionsJson,bool IsEnabled=true);
public sealed record WorkflowRun(Guid Id,Guid WorkflowId,Guid JobId,string TriggerEvent,string Status,int ActionIndex,string ContextJson,DateTimeOffset? NextActionAtUtc,string? LastError,DateTimeOffset CreatedAtUtc,DateTimeOffset UpdatedAtUtc,DateTimeOffset? CompletedAtUtc);
public sealed record WorkflowPreview(Guid WorkflowId,string WorkflowName,Guid JobId,string JobTitle,bool ConditionsMatch,IReadOnlyList<string> Actions);
