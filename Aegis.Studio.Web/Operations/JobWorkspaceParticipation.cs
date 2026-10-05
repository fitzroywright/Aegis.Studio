using System.Security.Claims;
using Aegis.Studio.Web.Media;
using Aegis.Studio.Web.Persistence;
using Npgsql;
namespace Aegis.Studio.Web.Operations;

public static class JobWorkspaceParticipation
{
    public const string Schema="""
        create table if not exists studio_job_workspace_links(
          job_id uuid not null references studio_jobs(id) on delete cascade,
          workspace_id uuid not null references studio_workspaces(id) on delete cascade,
          added_by text not null,added_at timestamptz not null default now(),primary key(job_id,workspace_id));
        create index if not exists ix_job_workspace_links_workspace on studio_job_workspace_links(workspace_id,job_id);
        """;
    public static void MapJobWorkspaceParticipation(this WebApplication app)
    {
        app.MapPut("/api/jobs/{jobId:guid}/participating-workspaces/{workspaceId:guid}",(Guid jobId,Guid workspaceId,ClaimsPrincipal user,JobAccessService jobs,WorkspaceAccessService workspaces,StudioDatabase db)=>
        {
            if(!jobs.HasPermission(user,jobId,"AdminJob")&&!jobs.HasPermission(user,jobId,"ManageJobMembers"))return Results.Forbid();
            if(!workspaces.HasPermission(user,workspaceId,"EditJob"))return Results.Forbid();
            using var c=db.OpenConnection();using var tx=c.BeginTransaction();
            string actor=user.FindFirstValue("login_name")??user.Identity?.Name??"Studio";
            if(MediaProductionStore.Execute(c,tx,"insert into studio_job_workspace_links(job_id,workspace_id,added_by) select $1,id,$3 from studio_workspaces where id=$2 and is_enabled on conflict do nothing",jobId,workspaceId,actor)>0)
                MediaProductionStore.Audit(c,tx,jobId,"WorkspaceParticipationAdded",actor,$"Workspace {workspaceId} participates in the shared job.");
            tx.Commit();return Results.NoContent();
        }).RequireAuthorization();
        app.MapDelete("/api/jobs/{jobId:guid}/participating-workspaces/{workspaceId:guid}",(Guid jobId,Guid workspaceId,ClaimsPrincipal user,JobAccessService jobs,StudioDatabase db)=>
        {
            if(!jobs.HasPermission(user,jobId,"AdminJob")&&!jobs.HasPermission(user,jobId,"ManageJobMembers"))return Results.Forbid();
            using var c=db.OpenConnection();using var tx=c.BeginTransaction();
            string actor=user.FindFirstValue("login_name")??user.Identity?.Name??"Studio";
            if(MediaProductionStore.Execute(c,tx,"delete from studio_job_workspace_links where job_id=$1 and workspace_id=$2",jobId,workspaceId)>0)
                MediaProductionStore.Audit(c,tx,jobId,"WorkspaceParticipationRemoved",actor,$"Workspace {workspaceId} removed from job participation.");
            tx.Commit();return Results.NoContent();
        }).RequireAuthorization();
    }
}
