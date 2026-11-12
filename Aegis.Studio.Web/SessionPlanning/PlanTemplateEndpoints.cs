using System.Security.Claims;
using System.Text.Json;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Operations;
namespace Aegis.Studio.Web.SessionPlanning;

public static class PlanTemplateEndpoints
{
    public static void MapPlanTemplates(this WebApplication app)
    {
        var admin = app.MapGroup("/api/admin/session-plan-templates").RequireAuthorization(p => p.RequireRole("Administrator"));
        admin.MapGet("", (StudioDatabase db) => Results.Ok(new PlanTemplateStore(db).List()));
        admin.MapPut("/{id:guid}", (Guid id, SavePlanTemplate request, ClaimsPrincipal user, StudioDatabase db) => Run(() => Results.Ok(new PlanTemplateStore(db).Save(id, request, user.Identity?.Name ?? "Staff"))));
        var jobs = app.MapGroup("/api/jobs/{job:guid}/session-plan").RequireAuthorization();
        jobs.MapGet("/templates", (Guid job, ClaimsPrincipal user, JobAccessService access, StudioDatabase db) => access.HasPermission(user, job, "EditJob") ? Run(() => Results.Ok(new PlanTemplateStore(db).Available(job))) : Results.Forbid());
        jobs.MapGet("/structure", (Guid job, ClaimsPrincipal user, JobAccessService access, StudioDatabase db) => access.HasPermission(user, job, "ViewJob") ? Results.Content(JsonSerializer.Serialize(new PlanTemplateStore(db).GetJob(job), new JsonSerializerOptions(JsonSerializerDefaults.Web)), "application/json") : Results.Forbid());
        jobs.MapPut("/structure", (Guid job, ApplyPlanTemplate request, ClaimsPrincipal user, JobAccessService access, StudioDatabase db) => access.HasPermission(user, job, "EditJob") ? Run(() => Results.Ok(new PlanTemplateStore(db).Apply(job, request, user.Identity?.Name ?? "Staff"))) : Results.Forbid());
    }
    private static IResult Run(Func<IResult> action)
    {
        try { return action(); }
        catch (KeyNotFoundException) { return Results.NotFound(new { error = "Template or job was not found." }); }
        catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
        catch (InvalidOperationException e) { return Results.Conflict(new { error = e.Message }); }
    }
}
