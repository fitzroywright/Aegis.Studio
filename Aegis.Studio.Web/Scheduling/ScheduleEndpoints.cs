using System.Security.Claims;
using Aegis.Studio.Web.Notifications;
using Aegis.Studio.Web.Operations;

namespace Aegis.Studio.Web.Scheduling;

public static class ScheduleEndpoints
{
    public static void MapStudioScheduling(this WebApplication app)
    {
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

        app.MapGet("/api/schedule/entries", (DateTimeOffset? from, DateTimeOffset? to, ClaimsPrincipal user, ScheduleStore store, JobAccessService access) =>
        {
            IReadOnlyList<ScheduleEntry> entries = store.GetEntries(from, to);
            if (user.IsInRole("Administrator")) return Results.Ok(entries);
            HashSet<Guid> allowed = access.GetAccessibleJobIds(user).ToHashSet();
            return Results.Ok(entries.Where(x => x.JobId.HasValue && allowed.Contains(x.JobId.Value)));
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
            JobEventNotifier notifier) =>
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
                if (value.JobId.HasValue)
                {
                    notifier.Publish(
                        value.JobId.Value,
                        "ScheduleChanged",
                        $"Scheduled: {value.Title}",
                        $"{value.Title} is scheduled for {value.StartsAtUtc:u} to {value.EndsAtUtc:u}{(string.IsNullOrWhiteSpace(value.Location) ? string.Empty : $" at {value.Location}")}.",
                        requesterEligible: true);
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
            JobEventNotifier notifier) =>
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
                if (value.JobId.HasValue)
                {
                    string verb = value.Status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) ? "Cancelled" : "Schedule updated";
                    notifier.Publish(
                        value.JobId.Value,
                        "ScheduleChanged",
                        $"{verb}: {value.Title}",
                        $"{value.Title} is {value.Status} for {value.StartsAtUtc:u} to {value.EndsAtUtc:u}{(string.IsNullOrWhiteSpace(value.Location) ? string.Empty : $" at {value.Location}")}.",
                        requesterEligible: true);
                }
                return Results.Ok(value);
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ScheduleConflictException exception) { return Results.Conflict(new { error = exception.Message, conflicts = exception.Conflicts }); }
            catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
        }).RequireAuthorization();
    }

    private static Guid[] ParseIds(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Guid.Parse).ToArray();
}
