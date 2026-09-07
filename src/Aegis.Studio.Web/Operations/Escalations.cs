using Aegis.Studio.Web.Notifications;
using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Operations;

public static class EscalationEndpoints
{
    public static void MapStudioEscalations(this WebApplication app)
    {
        StudioDatabase database = app.Services.GetRequiredService<StudioDatabase>();
        NotificationService notifications = app.Services.GetRequiredService<NotificationService>();
        EscalationStore store = new(database, notifications, app.Logger);
        store.EnsureSchema();

        RouteGroupBuilder group = app.MapGroup("/api/escalations").RequireAuthorization();
        group.MapGet("/open", (HttpContext context) => context.User.IsInRole("Administrator") ? Results.Ok(store.GetOpen()) : Results.Forbid());
        group.MapGet("/history", (HttpContext context, int? count) => context.User.IsInRole("Administrator") ? Results.Ok(store.GetHistory(Math.Clamp(count ?? 200,1,2000))) : Results.Forbid());
        group.MapPost("/{id:guid}/acknowledge", (Guid id, HttpContext context) =>
        {
            if (!context.User.IsInRole("Administrator")) return Results.Forbid();
            try { return Results.Ok(store.Acknowledge(id, context.User.Identity?.Name ?? "Administrator")); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });
        group.MapPost("/scan", (HttpContext context) =>
        {
            if (!context.User.IsInRole("Administrator")) return Results.Forbid();
            return Results.Ok(store.Scan());
        });

        CancellationToken token = app.Lifetime.ApplicationStopping;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try { store.Scan(); }
                catch (Exception exception) { app.Logger.LogError(exception, "Escalation scan failed."); }
                try { await Task.Delay(TimeSpan.FromMinutes(1), token); }
                catch (OperationCanceledException) { break; }
            }
        }, token);
    }
}

public sealed class EscalationStore
{
    private readonly StudioDatabase database;
    private readonly NotificationService notifications;
    private readonly ILogger logger;

    public EscalationStore(StudioDatabase database, NotificationService notifications, ILogger logger)
    {
        this.database = database;
        this.notifications = notifications;
        this.logger = logger;
    }

    public void EnsureSchema()
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            create table if not exists studio_escalations
            (
                id uuid primary key,
                source_type text not null,
                source_id uuid not null,
                job_id uuid null references studio_jobs(id) on delete cascade,
                level integer not null,
                subject text not null,
                detail text not null,
                opened_at_utc timestamptz not null,
                last_notified_at_utc timestamptz not null,
                acknowledged_at_utc timestamptz null,
                acknowledged_by text null,
                resolved_at_utc timestamptz null,
                unique(source_type,source_id,level)
            );
            create index if not exists ix_studio_escalations_open on studio_escalations(resolved_at_utc,acknowledged_at_utc,opened_at_utc);
            """;
        command.ExecuteNonQuery();
    }

    public EscalationScanResult Scan()
    {
        int created = 0;
        int resolved = ResolveNoLongerActive();
        foreach (EscalationCandidate candidate in GetCandidates())
        {
            int level = DetermineLevel(candidate.OverdueSinceUtc);
            if (EnsureEscalation(candidate, level)) created++;
        }
        return new EscalationScanResult(created, resolved, GetOpen().Count, DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<EscalationEntry> GetOpen()
    {
        using NpgsqlConnection connection=database.OpenConnection();using NpgsqlCommand command=connection.CreateCommand();command.CommandText="select id,source_type,source_id,job_id,level,subject,detail,opened_at_utc,last_notified_at_utc,acknowledged_at_utc,acknowledged_by,resolved_at_utc from studio_escalations where resolved_at_utc is null order by level desc,opened_at_utc";using NpgsqlDataReader reader=command.ExecuteReader();List<EscalationEntry> values=[];while(reader.Read())values.Add(Read(reader));return values;
    }

    public IReadOnlyList<EscalationEntry> GetHistory(int count)
    {
        using NpgsqlConnection connection=database.OpenConnection();using NpgsqlCommand command=connection.CreateCommand();command.CommandText="select id,source_type,source_id,job_id,level,subject,detail,opened_at_utc,last_notified_at_utc,acknowledged_at_utc,acknowledged_by,resolved_at_utc from studio_escalations order by opened_at_utc desc limit $1";command.Parameters.AddWithValue(count);using NpgsqlDataReader reader=command.ExecuteReader();List<EscalationEntry> values=[];while(reader.Read())values.Add(Read(reader));return values;
    }

    public EscalationEntry Acknowledge(Guid id,string userName)
    {
        using NpgsqlConnection connection=database.OpenConnection();using NpgsqlCommand command=connection.CreateCommand();command.CommandText="update studio_escalations set acknowledged_at_utc=coalesce(acknowledged_at_utc,now()),acknowledged_by=coalesce(acknowledged_by,$2) where id=$1";command.Parameters.AddWithValue(id);command.Parameters.AddWithValue(userName);if(command.ExecuteNonQuery()!=1)throw new KeyNotFoundException();return GetHistory(2000).First(x=>x.Id==id);
    }

    private IReadOnlyList<EscalationCandidate> GetCandidates()
    {
        List<EscalationCandidate> values=[];
        using NpgsqlConnection connection=database.OpenConnection();
        using(NpgsqlCommand command=connection.CreateCommand())
        {
            command.CommandText="select a.id,a.job_id,a.due_at_utc,j.title,a.title,a.status from studio_job_activities a join studio_jobs j on j.id=a.job_id where a.status not in ('Completed','Cancelled') and a.due_at_utc is not null and a.due_at_utc<now()";
            using NpgsqlDataReader reader=command.ExecuteReader();while(reader.Read())values.Add(new("Activity",reader.GetGuid(0),reader.GetGuid(1),reader.GetFieldValue<DateTimeOffset>(2),$"Overdue activity: {reader.GetString(4)}",$"{reader.GetString(3)} / {reader.GetString(4)} is {reader.GetString(5)} and overdue."));
        }
        using(NpgsqlCommand command=connection.CreateCommand())
        {
            command.CommandText="select r.id,r.job_id,r.updated_at_utc,d.name,coalesce(r.last_error,'Workflow failed') from studio_workflow_runs r join studio_workflow_definitions d on d.id=r.workflow_id where r.status='Failed'";
            using NpgsqlDataReader reader=command.ExecuteReader();while(reader.Read())values.Add(new("Workflow",reader.GetGuid(0),reader.GetGuid(1),reader.GetFieldValue<DateTimeOffset>(2),$"Workflow failed: {reader.GetString(3)}",reader.GetString(4)));
        }
        using(NpgsqlCommand command=connection.CreateCommand())
        {
            command.CommandText="select i.id,i.job_id,i.due_at_utc,i.invoice_number,i.client_name,i.total_amount,i.currency from studio_account_invoices i where i.status in ('Issued','Overdue') and i.due_at_utc<now()";
            using NpgsqlDataReader reader=command.ExecuteReader();while(reader.Read())values.Add(new("Invoice",reader.GetGuid(0),reader.GetGuid(1),reader.GetFieldValue<DateTimeOffset>(2),$"Overdue invoice: {reader.GetString(3)}",$"{reader.GetString(4)} owes {reader.GetString(6)} {reader.GetDecimal(5):N2}."));
        }
        return values;
    }

    private bool EnsureEscalation(EscalationCandidate candidate,int level)
    {
        using NpgsqlConnection connection=database.OpenConnection();using NpgsqlTransaction transaction=connection.BeginTransaction();Guid id=Guid.NewGuid();
        using NpgsqlCommand command=connection.CreateCommand();command.Transaction=transaction;command.CommandText="insert into studio_escalations(id,source_type,source_id,job_id,level,subject,detail,opened_at_utc,last_notified_at_utc) values($1,$2,$3,$4,$5,$6,$7,now(),now()) on conflict(source_type,source_id,level) do nothing";command.Parameters.AddWithValue(id);command.Parameters.AddWithValue(candidate.SourceType);command.Parameters.AddWithValue(candidate.SourceId);command.Parameters.AddWithValue(candidate.JobId);command.Parameters.AddWithValue(level);command.Parameters.AddWithValue(candidate.Subject);command.Parameters.AddWithValue(candidate.Detail);bool inserted=command.ExecuteNonQuery()==1;transaction.Commit();if(!inserted)return false;
        IReadOnlyList<string> recipients=GetRecipients(candidate.JobId,candidate.SourceType,level);foreach(string userName in recipients)notifications.PublishToUser(userName,"Escalation",$"[Escalation L{level}] {candidate.Subject}",candidate.Detail,"Job",candidate.JobId.ToString("D"));
        logger.LogWarning("Escalation L{Level} {SourceType} {SourceId} job {JobId} notified {RecipientCount} recipient(s)",level,candidate.SourceType,candidate.SourceId,candidate.JobId,recipients.Count);
        return true;
    }

    private IReadOnlyList<string> GetRecipients(Guid jobId,string sourceType,int level)
    {
        HashSet<string> recipients=new(StringComparer.OrdinalIgnoreCase);using NpgsqlConnection connection=database.OpenConnection();
        using(NpgsqlCommand command=connection.CreateCommand())
        {
            command.CommandText="select assigned_to from studio_jobs where id=$1 and assigned_to is not null";command.Parameters.AddWithValue(jobId);object? assigned=command.ExecuteScalar();if(assigned is string user&&!string.IsNullOrWhiteSpace(user))recipients.Add(user);
        }
        using(NpgsqlCommand command=connection.CreateCommand())
        {
            command.CommandText=level>=2
                ? "select user_name from studio_job_members where job_id=$1 and is_active and (admin_job or manage_production or record_financial_activity)"
                : "select user_name from studio_job_members where job_id=$1 and is_active and (manage_production or record_financial_activity)";
            command.Parameters.AddWithValue(jobId);using NpgsqlDataReader reader=command.ExecuteReader();while(reader.Read())recipients.Add(reader.GetString(0));
        }
        if(level>=2)
        {
            using NpgsqlCommand command=connection.CreateCommand();command.CommandText="select wm.user_name from studio_workspace_members wm join studio_jobs j on j.workspace_id=wm.workspace_id where j.id=$1 and wm.is_active and (wm.admin_workspace or wm.manage_production)";command.Parameters.AddWithValue(jobId);using NpgsqlDataReader reader=command.ExecuteReader();while(reader.Read())recipients.Add(reader.GetString(0));
        }
        if(sourceType=="Invoice")
        {
            using NpgsqlCommand command=connection.CreateCommand();command.CommandText="select m.user_name from studio_global_operational_role_members m join studio_global_operational_roles r on r.id=m.role_id where m.is_active and r.is_active and r.billing";using NpgsqlDataReader reader=command.ExecuteReader();while(reader.Read())recipients.Add(reader.GetString(0));
        }
        return recipients.ToList();
    }

    private int ResolveNoLongerActive()
    {
        using NpgsqlConnection connection=database.OpenConnection();using NpgsqlCommand command=connection.CreateCommand();command.CommandText="""
            update studio_escalations e set resolved_at_utc=now()
            where e.resolved_at_utc is null and (
              (e.source_type='Activity' and not exists(select 1 from studio_job_activities a where a.id=e.source_id and a.status not in ('Completed','Cancelled') and a.due_at_utc<now()))
              or (e.source_type='Workflow' and not exists(select 1 from studio_workflow_runs r where r.id=e.source_id and r.status='Failed'))
              or (e.source_type='Invoice' and not exists(select 1 from studio_account_invoices i where i.id=e.source_id and i.status in ('Issued','Overdue') and i.due_at_utc<now()))
            )
            """;return command.ExecuteNonQuery();
    }

    private static int DetermineLevel(DateTimeOffset since)
    {
        TimeSpan age=DateTimeOffset.UtcNow-since;if(age>=TimeSpan.FromHours(24))return 3;if(age>=TimeSpan.FromHours(4))return 2;return 1;
    }

    private static EscalationEntry Read(NpgsqlDataReader r)=>new(r.GetGuid(0),r.GetString(1),r.GetGuid(2),r.IsDBNull(3)?null:r.GetGuid(3),r.GetInt32(4),r.GetString(5),r.GetString(6),r.GetFieldValue<DateTimeOffset>(7),r.GetFieldValue<DateTimeOffset>(8),r.IsDBNull(9)?null:r.GetFieldValue<DateTimeOffset>(9),r.IsDBNull(10)?null:r.GetString(10),r.IsDBNull(11)?null:r.GetFieldValue<DateTimeOffset>(11));
}

public sealed record EscalationCandidate(string SourceType,Guid SourceId,Guid JobId,DateTimeOffset OverdueSinceUtc,string Subject,string Detail);
public sealed record EscalationEntry(Guid Id,string SourceType,Guid SourceId,Guid? JobId,int Level,string Subject,string Detail,DateTimeOffset OpenedAtUtc,DateTimeOffset LastNotifiedAtUtc,DateTimeOffset? AcknowledgedAtUtc,string? AcknowledgedBy,DateTimeOffset? ResolvedAtUtc);
public sealed record EscalationScanResult(int Created,int Resolved,int Open,DateTimeOffset ScannedAtUtc);
