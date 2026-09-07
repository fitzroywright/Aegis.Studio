namespace Aegis.Studio.Web.Scheduling;

public static class ScheduleEndpoints
{
    public static void MapStudioScheduling(this WebApplication app)
    {
        app.MapGet("/api/schedule/resources", (ScheduleStore store, bool? includeInactive) => Results.Ok(store.GetResources(includeInactive == true))).RequireAuthorization();
        app.MapPost("/api/schedule/resources", (ScheduleResourceRequest request, ScheduleStore store) =>
        {
            try { ScheduleResource value = store.SaveResource(request); return Results.Created($"/api/schedule/resources/{value.Id}", value); }
            catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
        }).RequireAuthorization();
        app.MapPut("/api/schedule/resources/{id:guid}", (Guid id, ScheduleResourceRequest request, ScheduleStore store) =>
        {
            try { return Results.Ok(store.SaveResource(request, id)); }
            catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
        }).RequireAuthorization();

        app.MapGet("/api/schedule/entries", (DateTimeOffset? from, DateTimeOffset? to, ScheduleStore store) => Results.Ok(store.GetEntries(from, to))).RequireAuthorization();
        app.MapGet("/api/schedule/conflicts", (DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc, string? resourceIds, ScheduleStore store) =>
        {
            Guid[] ids = ParseIds(resourceIds);
            return Results.Ok(store.FindConflicts(startsAtUtc, endsAtUtc, ids, null));
        }).RequireAuthorization();
        app.MapPost("/api/schedule/entries", (ScheduleEntryRequest request, ScheduleStore store) =>
        {
            try { ScheduleEntry value = store.Create(request); return Results.Created($"/api/schedule/entries/{value.Id}", value); }
            catch (ScheduleConflictException exception) { return Results.Conflict(new { error = exception.Message, conflicts = exception.Conflicts }); }
            catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
        }).RequireAuthorization();
        app.MapPut("/api/schedule/entries/{id:guid}", (Guid id, ScheduleEntryRequest request, ScheduleStore store) =>
        {
            try { return Results.Ok(store.Update(id, request)); }
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
