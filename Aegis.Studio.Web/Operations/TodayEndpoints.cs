using System.Diagnostics;
using System.Security.Claims;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Production;
using Aegis.Studio.Web.Scheduling;
using Npgsql;

namespace Aegis.Studio.Web.Operations;

public static class TodayEndpoints
{
    public static void MapStudioToday(this WebApplication app)
    {
        app.MapGet("/api/today/jobs", async (HttpContext context, StudioDatabase database, JobAccessService access, ILoggerFactory logs) =>
        {
            ILogger logger = logs.CreateLogger("Studio.Today");
            Guid[] allowed = Allowed(context, access, logger);
            Stopwatch timer = Stopwatch.StartNew();
            await using var connection = database.OpenConnection();
            await using var command = new NpgsqlCommand("select j.id,j.workspace_id,j.conversation_id,j.title,j.client_name,j.status,j.event_date,j.created_at_utc,j.assigned_to,j.client_id,c.display_name,coalesce(t.total,0),coalesce(t.completed,0),j.job_lead,exists(select 1 from studio_workflow_sessions ws join studio_workflow_crew wc on wc.session_id=ws.id where ws.job_id=j.id and lower(wc.user_name)=lower($2)) as is_crew_member from studio_jobs j left join studio_clients c on c.id=j.client_id left join lateral (select count(*) total,count(*) filter(where status='Completed') completed from studio_job_activities where job_id=j.id) t on true where j.id=any($1) order by j.created_at_utc desc", connection);
            command.Parameters.AddWithValue(allowed);
            command.Parameters.AddWithValue(Name(context.User));
            await using var reader = await command.ExecuteReaderAsync(context.RequestAborted);
            List<TodayJobSummary> jobs = [];
            while (await reader.ReadAsync(context.RequestAborted))
                jobs.Add(new(reader.GetGuid(0),reader.GetGuid(1),reader.GetGuid(2),reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.IsDBNull(6)?null:DateOnly.FromDateTime(reader.GetDateTime(6)),reader.GetFieldValue<DateTimeOffset>(7),reader.IsDBNull(8)?null:reader.GetString(8),reader.IsDBNull(9)?null:reader.GetGuid(9),reader.IsDBNull(10)?null:reader.GetString(10),reader.GetInt64(11),reader.GetInt64(12),reader.IsDBNull(13)?null:reader.GetString(13),reader.GetBoolean(14)));
            QueryComplete(context, logger, "job-list", timer, jobs.Count);
            return Results.Ok(jobs);
        }).RequireAuthorization();

        app.MapGet("/api/today/tasks", (HttpContext context, StudioDatabase database, JobAccessService access, ILoggerFactory logs) =>
        {
            ILogger logger = logs.CreateLogger("Studio.Today");
            HashSet<Guid> allowed = Allowed(context, access, logger).ToHashSet();
            Stopwatch timer = Stopwatch.StartNew();
            var tasks = new OperationalWorkStore(database).GetActivities(assignedTo: Name(context.User)).Where(t=>allowed.Contains(t.JobId)).ToList();
            QueryComplete(context, logger, "assigned-tasks", timer, tasks.Count);
            return Results.Ok(tasks);
        }).RequireAuthorization();


        app.MapGet("/api/today/schedule", (DateTimeOffset? from, DateTimeOffset? to, HttpContext context, ScheduleStore store, JobAccessService access, ILoggerFactory logs) =>
        {
            ILogger logger = logs.CreateLogger("Studio.Today");
            DateTimeOffset start=(from??DateTimeOffset.UtcNow.Date).ToUniversalTime();
            DateTimeOffset end=(to??start.AddDays(1)).ToUniversalTime();
            if (end <= start || (end-start).TotalDays > 31)
                return Results.BadRequest(new { error = "Choose a calendar period of at most 31 days." });
            HashSet<Guid>? allowed = context.User.IsInRole("Administrator") ? null : Allowed(context, access, logger).ToHashSet();
            Stopwatch timer=Stopwatch.StartNew();
            // Use the calendar's canonical records and visibility rules. A scheduled
            // session need not already have a WorkDay shoot record or assigned crew.
            var rows=store.GetEntries(start,end)
                .Where(e=>e.Status!="Cancelled" && (allowed is null || (e.JobId.HasValue && allowed.Contains(e.JobId.Value))))
                .ToArray();
            QueryComplete(context,logger,"personal-schedule",timer,rows.Length);
            return Results.Ok(rows);
        }).RequireAuthorization();

        app.MapGet("/api/today/media-summary", async (HttpContext context, StudioDatabase database, JobAccessService access, ILoggerFactory logs) =>
        {
            ILogger logger = logs.CreateLogger("Studio.Today");
            Guid[] allowed = Allowed(context, access, logger, true);
            Stopwatch timer = Stopwatch.StartNew();
            await using var connection = database.OpenConnection();
            await using var command = new NpgsqlCommand("""
                with keys as (
                    select job_id,asset_key from studio_media_assets where job_id=any($1)
                    union
                    select v.job_id,v.asset_key from studio_asset_versions v where v.job_id=any($1)
                      and v.asset_key not like 'unmatched/%' and not exists(select 1 from studio_media_unmatched u where u.version_id=v.id)
                ), counts as (select job_id,count(*) asset_count from keys group by job_id)
                select j.id,coalesce(c.asset_count,0),thumb.id
                from studio_jobs j left join counts c on c.job_id=j.id
                left join lateral (
                    select v.id from studio_asset_versions v where v.job_id=j.id and v.content_type like 'image/%'
                      and exists(select 1 from keys k where k.job_id=v.job_id and k.asset_key=v.asset_key)
                    order by v.asset_key,v.version_number desc limit 1
                ) thumb on true
                where j.id=any($1)
                """, connection);
            command.Parameters.AddWithValue(allowed);
            await using var reader = await command.ExecuteReaderAsync(context.RequestAborted);
            List<TodayMediaSummary> rows = [];
            while (await reader.ReadAsync(context.RequestAborted)) rows.Add(new(reader.GetGuid(0),reader.GetInt64(1),reader.IsDBNull(2)?null:reader.GetGuid(2)));
            QueryComplete(context, logger, "media-summary", timer, rows.Count);
            return Results.Ok(rows);
        }).RequireAuthorization();
    }

    private static Guid[] Allowed(HttpContext context, JobAccessService access, ILogger logger, bool attachments = false)
    {
        Stopwatch timer = Stopwatch.StartNew();
        Guid[] ids = access.GetAccessibleJobIds(context.User, attachments).ToArray();
        QueryComplete(context, logger, "authorization", timer, ids.Length);
        return ids;
    }
    private static string Name(ClaimsPrincipal user) => user.FindFirstValue("login_name") ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.Identity?.Name ?? throw new InvalidOperationException("Authenticated user has no login identifier.");
    private static void QueryComplete(HttpContext context, ILogger logger, string phase, Stopwatch timer, int count)
        => logger.LogInformation("Today startup phase Run={Run} Trace={Trace} Phase={Phase} Rows={Rows} ElapsedMs={ElapsedMs}",context.Request.Headers["X-Studio-Startup"].ToString(),context.TraceIdentifier,phase,count,timer.Elapsed.TotalMilliseconds);
}
public sealed record TodayMediaSummary(Guid JobId, long AssetCount, Guid? ThumbnailVersionId);

public sealed record TodayJobSummary(Guid Id,Guid WorkspaceId,Guid ConversationId,string Title,string ClientName,string Status,DateOnly? EventDate,DateTimeOffset CreatedAt,string? AssignedTo,Guid? ClientId,string? LinkedClientName,long TaskCount,long CompletedTaskCount,string? JobLead=null,bool IsCrewMember=false);

