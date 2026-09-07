using System.Security.Claims;
using Aegis.Studio.Web.Operations;

namespace Aegis.Studio.Web.Production;

public static class ProductionEndpoints
{
    public static void MapStudioProduction(this WebApplication app)
    {
        app.MapGet("/api/production/queue", (string? status, ClaimsPrincipal user, ProductionStore store, JobAccessService access) =>
        {
            HashSet<Guid> allowed = access.GetAccessibleJobIds(user).ToHashSet();
            return Results.Ok(store.GetQueue(status).Where(x => allowed.Contains(x.JobId)));
        }).RequireAuthorization();

        app.MapGet("/api/jobs/{jobId:guid}/production/tasks", (Guid jobId, ClaimsPrincipal user, ProductionStore store, JobAccessService access) =>
        {
            if (!access.HasPermission(user, jobId, "ViewJob") && !access.HasPermission(user, jobId, "ManageProduction")) return Results.Forbid();
            return Results.Ok(store.GetForJob(jobId));
        }).RequireAuthorization();

        app.MapPost("/api/jobs/{jobId:guid}/production/tasks", (Guid jobId, ProductionTaskRequest request, ClaimsPrincipal user, ProductionStore store, JobAccessService access) =>
        {
            if (!access.HasPermission(user, jobId, "ManageProduction")) return Results.Forbid();
            try { ProductionTask task = store.Create(jobId, request); return Results.Created($"/api/production/tasks/{task.Id}", task); }
            catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
        }).RequireAuthorization();

        app.MapPut("/api/production/tasks/{id:guid}", (Guid id, ProductionTaskRequest request, ClaimsPrincipal user, ProductionStore store, JobAccessService access) =>
        {
            ProductionTask? current = store.GetQueue().FirstOrDefault(x => x.Id == id);
            if (current is null) return Results.NotFound();
            if (!access.HasPermission(user, current.JobId, "ManageProduction")) return Results.Forbid();
            try { return Results.Ok(store.Update(id, request)); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
        }).RequireAuthorization();

        app.MapPost("/api/jobs/{jobId:guid}/production/template/{templateName}", (Guid jobId, string templateName, ClaimsPrincipal user, ProductionStore store, JobAccessService access) =>
        {
            if (!access.HasPermission(user, jobId, "ManageProduction")) return Results.Forbid();
            return Results.Ok(store.ApplyTemplate(jobId, templateName));
        }).RequireAuthorization();
    }
}
