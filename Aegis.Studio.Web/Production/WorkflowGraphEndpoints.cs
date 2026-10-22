using System.Security.Claims;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Workflow;
using static Aegis.Studio.Web.Production.CommercialEndpoints;

namespace Aegis.Studio.Web.Production;

public sealed record GraphPreviewRequest(WorkflowGraph Graph, Guid[] CatalogItemIds);
public static class WorkflowGraphEndpoints
{
    public static void MapJobWorkflowGraphs(this WebApplication app)
    {
        var admin = app.MapGroup("/api/admin/workflow-templates").RequireAuthorization(p => p.RequireRole("Administrator"));
        admin.MapGet("/drafts", (StudioDatabase db) => new WorkflowTemplateStore(db).Drafts());
        admin.MapPut("/{id:guid}/draft", (Guid id, SaveWorkflowDraft request, StudioDatabase db, ClaimsPrincipal user) => Run(() => Results.Ok(new WorkflowTemplateStore(db).SaveDraft(id, request, WorkflowStore.Name(user)))));
        admin.MapPost("/{id:guid}/publish", (Guid id, PublishWorkflowDraft request, StudioDatabase db, ClaimsPrincipal user) => Run(() => Results.Ok(new WorkflowTemplateStore(db).Publish(id, request.ExpectedRevision, WorkflowStore.Name(user)))));
        admin.MapGet("/{id:guid}/versions", (Guid id, StudioDatabase db) => new WorkflowTemplateStore(db).Versions(id));
        admin.MapPost("/validate", (WorkflowGraph graph) => Results.Ok(WorkflowGraphEngine.Validate(graph)));
        app.MapPost("/api/workflow-graphs/preview", (GraphPreviewRequest request) => Run(() =>
        {
            var issues = WorkflowGraphEngine.Validate(request.Graph);
            if (issues.Length > 0) return Results.BadRequest(new { error = string.Join("\n", issues.Select(i => $"{i.NodeId}: {i.Message}")), issues });
            return Results.Ok(WorkflowGraphEngine.Evaluate(request.Graph, new Dictionary<string, string>(), request.CatalogItemIds.ToHashSet()));
        })).RequireAuthorization();
        app.MapGet("/api/jobs/{job:guid}/shoot-sessions", (Guid job, ClaimsPrincipal user, JobAccessService access, StudioDatabase db) => Run(() =>
        {
            if (!access.HasPermission(user, job, "ViewJob")) return Results.Forbid();
            using var connection = db.OpenConnection();
            using var command = new Npgsql.NpgsqlCommand("""
                select ws.id, ws.title
                from studio_workflow_sessions ws
                where ws.job_id = $1
                order by ws.created_at_utc, ws.id
                """, connection);
            command.Parameters.AddWithValue(job);
            using var reader = command.ExecuteReader();
            var sessions = new List<object>();
            while (reader.Read()) sessions.Add(new { id = reader.GetGuid(0), title = reader.GetString(1) });
            return Results.Ok(sessions);
        })).RequireAuthorization();
        app.MapGet("/api/jobs/{job:guid}/workflow-graph", (Guid job, ClaimsPrincipal user, JobAccessService access, StudioDatabase db) => Run(() =>
        {
            if (!access.HasPermission(user, job, "ViewJob")) return Results.Forbid();
            return Results.Ok(new JobGraphStore(db).Get(job));
        })).RequireAuthorization();
        app.MapPost("/api/jobs/{job:guid}/workflow-graph/actions", (Guid job, GraphJobActionRequest request, ClaimsPrincipal user, WorkflowStore workflow) => Run(() =>
        {
            if (request.Action != "confirm-booking") throw new ArgumentException("Choose a supported workflow graph action.");
            var command = new WorkflowCommand("confirm", request.ExpectedRevision, request.OperationId, Reason: request.Reason);
            return Results.Ok(workflow.Execute(job, command, user));
        })).RequireAuthorization();
        app.MapPost("/api/jobs/{job:guid}/workflow-graph/tasks", (Guid job, GraphTaskCommand request, ClaimsPrincipal user, JobAccessService access, StudioDatabase db) => Run(() =>
        {
            if (!access.HasPermission(user, job, "EditJob") && !access.HasPermission(user, job, "ManageProduction")) return Results.Forbid();
            new JobGraphStore(db).TransitionTask(job, request, WorkflowStore.Name(user));
            return Results.Ok(new JobGraphStore(db).Get(job));
        })).RequireAuthorization();

        app.MapGet("/api/jobs/{job:guid}/workflow-graph/impact/{template:guid}", (Guid job, Guid template, ClaimsPrincipal user, WorkflowStore workflow, StudioDatabase db) => Run(() =>
        {
            if (!workflow.CanManage(user, job) && !workflow.IsFrontDesk(user)) return Results.Forbid();
            return Results.Ok(new JobGraphStore(db).Impact(job, template));
        })).RequireAuthorization();
        app.MapPut("/api/jobs/{job:guid}/workflow-graph/services", (Guid job, WorkflowServicesRequest request, ClaimsPrincipal user, WorkflowStore workflow, StudioDatabase db) => Run(() =>
        {
            if (!workflow.CanManage(user, job) && !workflow.IsFrontDesk(user)) return Results.Forbid();
            new JobGraphStore(db).SetServices(job, request, WorkflowStore.Name(user)); return Results.NoContent();
        })).RequireAuthorization();
        app.MapGet("/api/production/workflow-tasks", (bool? mine, string? state, Guid? jobId, ClaimsPrincipal user, JobAccessService access, StudioDatabase db) => Run(() =>
        {
            var store = new JobGraphStore(db); string actor = WorkflowStore.Name(user);
            var rows = access.GetProductionJobIds(user).Where(id => !jobId.HasValue || id == jobId)
                .Select(store.Get).Where(v => v is not null).SelectMany(v => v!.Tasks.Select(t => new { v.JobId, v.Revision, task = t }))
                .Where(r => mine != true || string.Equals(r.task.AssignedTo, actor, StringComparison.OrdinalIgnoreCase))
                .Where(r => string.IsNullOrWhiteSpace(state) || r.task.Eligibility == state).ToArray();
            return Results.Ok(rows);
        })).RequireAuthorization();
    }
}
