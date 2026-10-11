using System.Security.Claims;
using Aegis.Studio.Web.Media;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Npgsql;
namespace Aegis.Studio.Web.FrontDesk;

public static class StudioFulfillment
{
    public static void MapStudioFulfillment(this WebApplication app)
    {
        using(var c=app.Services.GetRequiredService<StudioDatabase>().OpenConnection())
        using(var cmd=new NpgsqlCommand("""
            create table if not exists studio_job_fulfillment(
              operation_id uuid primary key,job_id uuid not null unique references studio_jobs(id),
              method text not null,recipient text not null,recorded_by text not null,recorded_at timestamptz not null default now());
            """,c))cmd.ExecuteNonQuery();
        app.MapPost("/api/frontdesk/jobs/{jobId:guid}/fulfillment",(Guid jobId,FulfillmentRequest request,ClaimsPrincipal user,Aegis.Studio.Web.Workflow.WorkflowStore workflow)=>Aegis.Studio.Web.Workflow.WorkflowEndpoints.Run(()=>
        {
            Validate(request);
            if(request.ExpectedRevision is not long revision)throw new ArgumentException("Refresh the Job before recording delivery.");
            var result=workflow.Execute(jobId,new("deliver",revision,request.OperationId,Method:request.Method,Reference:request.Recipient.Trim()),user);
            return Results.Ok(new{status=result.GetProperty("stage").GetString(),data=result});
        })).RequireAuthorization();
    }
    public static void Validate(FulfillmentRequest request)
    {
        if(request.OperationId==Guid.Empty)throw new ArgumentException("An operation reference is required for safe retries.");
        if(request.Method is not ("Collected" or "Delivered"))throw new ArgumentException("Choose collection or delivery.");
        if(string.IsNullOrWhiteSpace(request.Recipient)||request.Recipient.Trim().Length>200)throw new ArgumentException("Record who received the completed order.");
    }
}
public sealed record FulfillmentRequest(Guid OperationId,string Method,string Recipient,long? ExpectedRevision=null);
