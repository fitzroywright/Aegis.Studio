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
            Guid? jobId = request.JobId ?? current?.JobId;
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
            access.EnsureAssignedMember(effectiveJobId, photographer.UserName);
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

