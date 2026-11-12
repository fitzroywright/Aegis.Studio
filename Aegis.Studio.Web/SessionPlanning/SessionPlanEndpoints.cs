using System.Security.Claims;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Operations;

namespace Aegis.Studio.Web.SessionPlanning;

public static class SessionPlanEndpoints
{
    public static void MapSubjectPlanning(this RouteGroupBuilder plans)
    {
        plans.MapPut("/people/{subject:guid}", (Guid job, Guid subject, SubjectPlan request, ClaimsPrincipal user, StudioDatabase db, JobAccessService access) =>
        {
            if (!access.HasPermission(user, job, "EditJob")) return Results.Forbid();
            try { return Results.Ok(new SubjectPlanStore(db).Save(job, subject, request, user.Identity?.Name ?? "Staff", access.HasPermission(user, job, "ManageProduction"))); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
            catch (InvalidOperationException e) { return Results.Conflict(new { error = e.Message }); }
        });
    }
}
