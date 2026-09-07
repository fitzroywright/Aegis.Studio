namespace Aegis.Studio.Web.Production;

public static class ProductionEndpoints
{
    public static void MapStudioProduction(this WebApplication app)
    {
        app.MapGet("/api/production/queue", (string? status, ProductionStore store) => Results.Ok(store.GetQueue(status))).RequireAuthorization();
        app.MapGet("/api/jobs/{jobId:guid}/production/tasks", (Guid jobId, ProductionStore store) => Results.Ok(store.GetForJob(jobId))).RequireAuthorization();
        app.MapPost("/api/jobs/{jobId:guid}/production/tasks", (Guid jobId, ProductionTaskRequest request, ProductionStore store) =>
        {
            try { ProductionTask task = store.Create(jobId, request); return Results.Created($"/api/production/tasks/{task.Id}", task); }
            catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
        }).RequireAuthorization();
        app.MapPut("/api/production/tasks/{id:guid}", (Guid id, ProductionTaskRequest request, ProductionStore store) =>
        {
            try { return Results.Ok(store.Update(id, request)); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
        }).RequireAuthorization();
        app.MapPost("/api/jobs/{jobId:guid}/production/template/{templateName}", (Guid jobId, string templateName, ProductionStore store) => Results.Ok(store.ApplyTemplate(jobId, templateName))).RequireAuthorization();
    }
}
