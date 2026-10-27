using System.Security.Claims;
using Aegis.Studio.Web.Notifications;
using Aegis.Studio.Web.Messaging;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Production;
using Npgsql;

namespace Aegis.Studio.Web.Scheduling;

public static class ScheduleEndpoints
{
    public static void MapStudioScheduling(this WebApplication app)
    {
        app.Services.GetRequiredService<ScheduleStore>().EnsureOperationalSchema();
        // Calendar payloads deliberately omit full Job aggregates, media and financial ledgers.
        app.MapGet("/api/schedule/worker-data", (DateTimeOffset from, DateTimeOffset to, bool? includeInactive, ClaimsPrincipal user, ScheduleStore store, JobAccessService access, StudioDatabase database, Aegis.Studio.Web.Workers.WorkerDirectory directory) =>
        {
            if (to <= from || (to-from).TotalDays > 31) return Results.BadRequest(new { error = "Choose a calendar period of at most 31 days." });
            HashSet<Guid>? allowed=user.IsInRole("Administrator")?null:access.GetAccessibleJobIds(user).ToHashSet();
            var visible=store.GetEntries(from,to).Where(e=>allowed is null||(e.JobId.HasValue&&allowed.Contains(e.JobId.Value))).ToArray();
            var roles=directory.GetTypes(true);
            var roleNames=roles.ToDictionary(r=>r.Code,r=>r.DisplayName,StringComparer.OrdinalIgnoreCase);
            var photographers=new PhotographerStore(database).GetAll(true).ToDictionary(p=>p.UserName,StringComparer.OrdinalIgnoreCase);
            var roster=new Dictionary<string,WorkerCalendarPerson>(StringComparer.OrdinalIgnoreCase);
            WorkerCalendarPerson Register(string userName,string displayName,string role,bool active)
            {
                role=role.ToUpperInvariant();string key=role+":"+userName.ToLowerInvariant();
                if(roster.TryGetValue(key,out var existing))return existing;
                string resource=role=="PHOTOGRAPHER"&&photographers.TryGetValue(userName,out var photo)?photo.ScheduleResourceId.ToString("D"):"worker:"+key;
                var person=new WorkerCalendarPerson(userName,displayName,role,roleNames.GetValueOrDefault(role,role),active,resource);
                roster[key]=person;return person;
            }
            foreach(var worker in directory.GetWorkers(includeInactive:true))Register(worker.UserName,worker.DisplayName,worker.JobRoleCode,worker.IsActive);
            foreach(var photo in photographers.Values)Register(photo.UserName,photo.DisplayName,"PHOTOGRAPHER",photo.IsActive);
            var entries=new List<object>(visible);
            var assignedResources=visible.SelectMany(e=>e.ResourceIds).Select(id=>id.ToString("D")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var jobIds=visible.Where(e=>e.JobId.HasValue).Select(e=>e.JobId!.Value).ToHashSet();
            using var connection=database.OpenConnection();
            using(var assignments=new NpgsqlCommand("""
                select a.id,a.job_id,a.worker_user_name,w.display_name,a.worker_type_code,w.is_active,
                       a.responsibility,a.starts_at_utc,a.ends_at_utc,a.status,j.title
                from studio_job_worker_assignments a
                join studio_workers w on w.user_name=a.worker_user_name
                join studio_jobs j on j.id=a.job_id
                where a.starts_at_utc<$2 and a.ends_at_utc>$1 and a.status<>'Cancelled'
                  and ($3 or a.job_id=any($4))
                order by a.starts_at_utc,a.id
                """,connection))
            {
                assignments.Parameters.AddWithValue(from.ToUniversalTime());assignments.Parameters.AddWithValue(to.ToUniversalTime());
                assignments.Parameters.AddWithValue(allowed is null);assignments.Parameters.AddWithValue(allowed?.ToArray()??Array.Empty<Guid>());
                using var reader=assignments.ExecuteReader();
                while(reader.Read())
                {
                    var person=Register(reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetBoolean(5));
                    Guid jobId=reader.GetGuid(1);var start=reader.GetFieldValue<DateTimeOffset>(7);var end=reader.GetFieldValue<DateTimeOffset>(8);
                    if(end<=start)continue;
                    assignedResources.Add(person.ScheduleResourceId);jobIds.Add(jobId);
                    // A photographer's linked calendar entry already represents this exact assignment.
                    if(person.WorkerTypeCode=="PHOTOGRAPHER"&&visible.Any(e=>e.JobId==jobId&&e.StartsAtUtc==start&&e.EndsAtUtc==end&&e.ResourceIds.Any(id=>id.ToString("D")==person.ScheduleResourceId)))continue;
                    string responsibility=reader.GetString(6);
                    entries.Add(new WorkerCalendarEntry("assignment:"+reader.GetGuid(0).ToString("D"),jobId,
                        reader.GetString(10)+(string.IsNullOrWhiteSpace(responsibility)?"":" · "+responsibility),start,end,null,reader.GetString(9),new[]{person.ScheduleResourceId}));
                }
            }
            var workers=roster.Values.Where(p=>includeInactive==true||p.IsActive||assignedResources.Contains(p.ScheduleResourceId)).OrderBy(p=>p.WorkerTypeLabel).ThenBy(p=>p.DisplayName).ToArray();
            var jobs=new List<object>();
            using(var command=new NpgsqlCommand("""
                select j.id,j.title,j.client_name,j.workspace_id,
                  array(select l.workspace_id from studio_job_workspace_links l where l.job_id=j.id)
                from studio_jobs j where j.id=any($1)
                """,connection))
            {
                command.Parameters.AddWithValue(jobIds.ToArray());using var reader=command.ExecuteReader();
                while(reader.Read())jobs.Add(new{id=reader.GetGuid(0),title=reader.GetString(1),clientName=reader.GetString(2),workspaceId=reader.GetGuid(3),participatingWorkspaceIds=reader.GetFieldValue<Guid[]>(4)});
            }
            return Results.Ok(new{workers,roles,entries,jobs});
        }).RequireAuthorization();

        app.MapGet("/api/schedule/worker-availability", (DateTimeOffset from, DateTimeOffset to, StudioDatabase database) =>
        {
            if (to <= from || (to - from).TotalDays > 31) return Results.BadRequest(new { error = "Choose a calendar period of at most 31 days." });
            using var connection = database.OpenConnection();
            using var command = new NpgsqlCommand("""
                select a.id,a.user_name,a.starts_at_utc,a.ends_at_utc,a.availability_type
                from studio_photographer_availability a
                where a.ends_at_utc>$1 and a.starts_at_utc<$2
                  and exists(select 1 from studio_photographers p where lower(p.user_name)=lower(a.user_name))
                order by a.user_name,a.starts_at_utc
                """, connection);
            command.Parameters.AddWithValue(from.ToUniversalTime());
            command.Parameters.AddWithValue(to.ToUniversalTime());
            using var reader = command.ExecuteReader();
            var rows = new List<object>();
            while (reader.Read()) rows.Add(new { id = reader.GetGuid(0), userName = reader.GetString(1), startsAtUtc = reader.GetFieldValue<DateTimeOffset>(2), endsAtUtc = reader.GetFieldValue<DateTimeOffset>(3), availabilityType = reader.GetString(4) });
            return Results.Ok(rows);
        }).RequireAuthorization();

        app.MapGet("/api/schedule/resources", (ScheduleStore store, bool? includeInactive) => Results.Ok(store.GetResources(includeInactive == true))).RequireAuthorization();
        app.MapPost("/api/schedule/resources", (ScheduleResourceRequest request, ClaimsPrincipal user, ScheduleStore store) =>
        {
            if (!user.IsInRole("Administrator")) return Results.Forbid();
            try { ScheduleResource value = store.SaveResource(request); return Results.Created($"/api/schedule/resources/{value.Id}", value); }
            catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
        }).RequireAuthorization();
        app.MapPut("/api/schedule/resources/{id:guid}", (Guid id, ScheduleResourceRequest request, ClaimsPrincipal user, ScheduleStore store) =>
        {
            if (!user.IsInRole("Administrator")) return Results.Forbid();
            try { return Results.Ok(store.SaveResource(request, id)); }
            catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
        }).RequireAuthorization();

        app.MapGet("/api/schedule/entries", (DateTimeOffset? from, DateTimeOffset? to, Guid? jobId, ClaimsPrincipal user, ScheduleStore store, JobAccessService access) =>
        {
            if(jobId.HasValue&&!access.HasPermission(user,jobId.Value,"ViewJob"))return Results.Forbid();
            IReadOnlyList<ScheduleEntry> entries = store.GetEntries(from, to, jobId);
            if (user.IsInRole("Administrator")) return Results.Ok(entries);
            HashSet<Guid> allowed = access.GetAccessibleJobIds(user).ToHashSet();
            return Results.Ok(entries.Where(x => x.JobId.HasValue && allowed.Contains(x.JobId.Value)));
        }).RequireAuthorization();

        app.MapGet("/api/schedule/capacity", (
            DateTimeOffset from,
            DateTimeOffset to,
            ClaimsPrincipal user,
            ScheduleStore store,
            WorkspaceAccessService workspaceAccess) =>
        {
            bool allowed=user.IsInRole("Administrator") ||
                workspaceAccess.GetAccessibleWorkspaceIds(user).Any(id =>
                    workspaceAccess.HasPermission(user,id,"ManageSchedule") ||
                    workspaceAccess.HasPermission(user,id,"ManageProduction"));
            if(!allowed) return Results.Forbid();
            try{return Results.Ok(store.GetStaffCapacity(from,to));}
            catch(InvalidOperationException ex){return Results.BadRequest(new{error=ex.Message});}
        }).RequireAuthorization();

        app.MapPost("/api/schedule/entries/{id:guid}/reassign", (
            Guid id,
            ScheduleReassignmentRequest request,
            ClaimsPrincipal user,
            ScheduleStore store,
            JobAccessService access,
            StudioDatabase database,
            StudioOperationsFlowPublisher flows,
            StudioInAppMessageStore messages) =>
        {
            ScheduleEntry? current=store.GetEntries(DateTimeOffset.UtcNow.AddYears(-10),DateTimeOffset.UtcNow.AddYears(10)).FirstOrDefault(x=>x.Id==id);
            if(current is null) return Results.NotFound();
            Guid? jobId=current.JobId??ResolveBookingJobId(current.BookingId,database);
            if(jobId is not Guid effectiveJobId || !access.HasPermission(user,effectiveJobId,"ManageSchedule")) return Results.Forbid();
            try
            {
                ScheduleAssignmentChange change=store.Reassign(id,request.ResourceIds??[],CurrentUser(user),request.Reason);
                ScheduleEntry updated=store.GetEntries(current.StartsAtUtc.AddMinutes(-1),current.EndsAtUtc.AddMinutes(1)).First(x=>x.Id==id);
                EnsureAssignedPhotographers(updated,database,access);
                string detail=string.IsNullOrWhiteSpace(request.Reason)?"Schedule resources reassigned.":"Schedule resources reassigned: "+request.Reason.Trim();
                flows.TryEnqueueJobFlow(effectiveJobId.ToString("D"),"Assigned",effectiveJobId.ToString("D"),detail);
                RecordScheduleChange(effectiveJobId,user,database,messages,detail);
                return Results.Ok(new{assignment=updated,change});
            }
            catch(ScheduleConflictException ex){return Results.Conflict(new{error=ex.Message,conflicts=ex.Conflicts});}
            catch(InvalidOperationException ex){return Results.BadRequest(new{error=ex.Message});}
        }).RequireAuthorization();

        app.MapGet("/api/schedule/entries/{id:guid}/assignment-history", (
            Guid id,
            ClaimsPrincipal user,
            ScheduleStore store,
            JobAccessService access,
            StudioDatabase database) =>
        {
            ScheduleEntry? current=store.GetEntries(DateTimeOffset.UtcNow.AddYears(-10),DateTimeOffset.UtcNow.AddYears(10)).FirstOrDefault(x=>x.Id==id);
            if(current is null) return Results.NotFound();
            Guid? jobId=current.JobId??ResolveBookingJobId(current.BookingId,database);
            if(jobId is not Guid effectiveJobId || !access.HasPermission(user,effectiveJobId,"ManageSchedule")) return Results.Forbid();
            return Results.Ok(store.GetAssignmentHistory(id));
        }).RequireAuthorization();

        app.MapGet("/api/schedule/conflicts", (DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc, string? resourceIds, ScheduleStore store) =>
        {
            Guid[] ids = ParseIds(resourceIds);
            return Results.Ok(store.FindConflicts(startsAtUtc, endsAtUtc, ids, null));
        }).RequireAuthorization();

        app.MapPost("/api/schedule/entries", (
            ScheduleEntryRequest request,
            ClaimsPrincipal user,
            ScheduleStore store,
            JobAccessService access,
            JobEventNotifier notifier,
            StudioDatabase database,
            StudioOperationsFlowPublisher flows,
            StudioInAppMessageStore messages) =>
        {
            if (!request.JobId.HasValue)
            {
                if (!user.IsInRole("Administrator")) return Results.Forbid();
            }
            else if (!access.HasPermission(user, request.JobId.Value, "ManageSchedule"))
            {
                return Results.Forbid();
            }
            try
            {
                ScheduleEntry value = store.Create(request);
                EnsureAssignedPhotographers(value, database, access);
                Guid? createdJobId=value.JobId??ResolveBookingJobId(value.BookingId,database);
                if(createdJobId is Guid createdJob)
                {
                    flows.TryEnqueueJobFlow(createdJob.ToString("D"),"Created",createdJob.ToString("D"));
                    if(value.ResourceIds.Count>0) flows.TryEnqueueJobFlow(createdJob.ToString("D"),"Assigned",createdJob.ToString("D"));
                }
                if (value.JobId.HasValue)
                {
                    string detail=$"{value.Title} scheduled for {value.StartsAtUtc:u} to {value.EndsAtUtc:u}{(string.IsNullOrWhiteSpace(value.Location)?string.Empty:$" at {value.Location}")}.";
                    notifier.Publish(
                        value.JobId.Value,
                        "ScheduleChanged",
                        $"Scheduled: {value.Title}",
                        detail,
                        requesterEligible: true);
                    RecordScheduleChange(value.JobId.Value,user,database,messages,detail);
                }
                return Results.Created($"/api/schedule/entries/{value.Id}", value);
            }
            catch (ScheduleConflictException exception) { return Results.Conflict(new { error = exception.Message, conflicts = exception.Conflicts }); }
            catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
        }).RequireAuthorization();

        app.MapPut("/api/schedule/entries/{id:guid}", (
            Guid id,
            ScheduleEntryRequest request,
            ClaimsPrincipal user,
            ScheduleStore store,
            JobAccessService access,
            JobEventNotifier notifier,
            StudioDatabase database,
            StudioOperationsFlowPublisher flows,
            StudioInAppMessageStore messages) =>
        {
            ScheduleEntry? current = store.GetEntries(DateTimeOffset.UtcNow.AddYears(-5), DateTimeOffset.UtcNow.AddYears(5)).FirstOrDefault(x => x.Id == id);
            if(current is null)return Results.NotFound();
            var previousJob=current.JobId??ResolveBookingJobId(current.BookingId,database);
            if(previousJob is Guid previous&&!access.HasPermission(user,previous,"ManageSchedule"))return Results.Forbid();
            Guid? jobId = request.JobId ?? current.JobId;
            if (!jobId.HasValue)
            {
                if (!user.IsInRole("Administrator")) return Results.Forbid();
            }
            else if (!access.HasPermission(user, jobId.Value, "ManageSchedule"))
            {
                return Results.Forbid();
            }
            try
            {
                ScheduleEntry value = store.Update(id, request);
                EnsureAssignedPhotographers(value, database, access);
                Guid? updatedJobId=value.JobId??ResolveBookingJobId(value.BookingId,database);
                if(updatedJobId is Guid updatedJob && value.ResourceIds.Count>0)
                    flows.TryEnqueueJobFlow(updatedJob.ToString("D"),"Assigned",updatedJob.ToString("D"),"Schedule assignment updated.");
                if (value.JobId.HasValue)
                {
                    string verb = value.Status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) ? "Cancelled" : "Schedule updated";
                    string detail=$"{value.Title} is {value.Status} for {value.StartsAtUtc:u} to {value.EndsAtUtc:u}{(string.IsNullOrWhiteSpace(value.Location)?string.Empty:$" at {value.Location}")}.";
                    notifier.Publish(
                        value.JobId.Value,
                        "ScheduleChanged",
                        $"{verb}: {value.Title}",
                        detail,
                        requesterEligible: true);
                    RecordScheduleChange(value.JobId.Value,user,database,messages,detail);
                }
                return Results.Ok(value);
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ScheduleConflictException exception) { return Results.Conflict(new { error = exception.Message, conflicts = exception.Conflicts }); }
            catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
        }).RequireAuthorization();
    }

    private static void RecordScheduleChange(Guid jobId, ClaimsPrincipal user, StudioDatabase database, StudioInAppMessageStore messages, string detail)
    {
        string actor=CurrentUser(user);
        using(NpgsqlConnection connection=database.OpenConnection())
        {
            using NpgsqlCommand audit=connection.CreateCommand();
            audit.CommandText="insert into studio_operational_audit(id,job_id,action,user_name,detail,created_at_utc) values($1,$2,'ScheduleChanged',$3,$4,now())";
            audit.Parameters.AddWithValue(Guid.NewGuid());audit.Parameters.AddWithValue(jobId);audit.Parameters.AddWithValue(actor);audit.Parameters.AddWithValue(detail);audit.ExecuteNonQuery();
            using NpgsqlCommand workspace=connection.CreateCommand();
            workspace.CommandText="select workspace_id from studio_jobs where id=$1";
            workspace.Parameters.AddWithValue(jobId);
            if(workspace.ExecuteScalar() is Guid workspaceId)
            {
                try
                {
                    StudioMessageThread thread=messages.GetOrCreateScoped(user,"Workspace",workspaceId);
                    messages.AddMessage(thread.Id,user,$"Schedule change: {detail}");
                }
                catch(UnauthorizedAccessException){ }
            }
        }
    }

    private static void EnsureAssignedPhotographers(ScheduleEntry entry, StudioDatabase database, JobAccessService access)
    {
        Guid? jobId = entry.JobId ?? ResolveBookingJobId(entry.BookingId, database);
        if (jobId is not Guid effectiveJobId || entry.ResourceIds.Count == 0) return;

        PhotographerStore photographers = new(database);
        foreach (PhotographerProfile photographer in photographers.GetByScheduleResourceIds(entry.ResourceIds))
        {
            if (!photographer.IsActive) continue;

        }
    }

    private static Guid? ResolveBookingJobId(Guid? bookingId, StudioDatabase database)
    {
        if (bookingId is not Guid id) return null;
        using Npgsql.NpgsqlConnection connection = database.OpenConnection();
        using Npgsql.NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select job_id from studio_bookings where id=$1";
        command.Parameters.AddWithValue(id);
        return command.ExecuteScalar() is Guid jobId ? jobId : null;
    }

    private static string CurrentUser(ClaimsPrincipal user)=>
        user.FindFirstValue("login_name")??user.FindFirstValue(ClaimTypes.NameIdentifier)??user.Identity?.Name??"unknown";

    private static Guid[] ParseIds(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Guid.Parse).ToArray();
}

public sealed record ScheduleReassignmentRequest(IReadOnlyList<Guid>? ResourceIds,string? Reason);




public sealed record WorkerCalendarPerson(string UserName,string DisplayName,string WorkerTypeCode,string WorkerTypeLabel,bool IsActive,string ScheduleResourceId);
public sealed record WorkerCalendarEntry(string Id,Guid JobId,string Title,DateTimeOffset StartsAtUtc,DateTimeOffset EndsAtUtc,string? Location,string Status,IReadOnlyList<string> ResourceIds);
