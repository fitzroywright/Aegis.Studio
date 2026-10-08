using System.Security.Claims;
using Aegis.Studio.Web.Workflow;
using Aegis.Studio.Web.Persistence;
using Common.Storage;
namespace Aegis.Studio.Web.Operations;

// Compatibility names map to the revised source and finished-work approval process.
public static class JobLifecycleRules
{
    public static string Canonical(string action)=>action switch {"submit-review"=>"submit-source","approve"=>"accept-source","finish-production"=>"submit-finished",_=>action};
    public static string Target(string action)=>Canonical(action) switch {"submit-source"=>"Awaiting Production Review","accept-source"=>"Active Production","submit-finished"=>"Awaiting Approval","approve-finished"=>"Ready for Delivery","request-changes"=>"Changes Requested","publish"=>"Ready for Delivery",_=>throw new ArgumentException("Choose a supported action.")};
    public static bool Allowed(string status,string action)
    {try{if(action=="publish")return status=="Ready for Delivery";WorkflowRules.Target(status,Canonical(action),status=="Changes Requested"?"Source":null);return true;}catch(ArgumentException){return false;}}
    public static void Validate(string status,string action,int mediaCount,bool openProduction,bool publishConfigured)
    {
        if(!Allowed(status,action))throw new ArgumentException("Refresh the Job before changing its stage.");
        if(action!="request-changes"&&mediaCount==0)throw new ArgumentException("Upload completed media first.");
        if(Canonical(action)=="submit-finished"&&openProduction)throw new ArgumentException("Resolve required production tasks first.");
        if(action=="publish"&&!publishConfigured)throw new ArgumentException("Configure the gallery provider first.");
    }
}
public sealed record JobLifecycleRequest(string Action,string? Note,string ExpectedStatus,Guid OperationId,long? ExpectedRevision=null);
public static class JobLifecycleEndpoints
{
    public static void MapJobLifecycle(this WebApplication app)
    {
        app.MapGet("/api/jobs/{jobId:guid}/workflow",(Guid jobId,ClaimsPrincipal user,WorkflowStore workflow,IConfiguration configuration)=>WorkflowEndpoints.Run(()=>
        {
            var view=System.Text.Json.JsonSerializer.SerializeToElement(workflow.View(jobId,user));var data=view.GetProperty("data");
            // Assignment/review controls are centralized in the Job panel; this legacy summary retains gallery handoff.
            return Results.Ok(new{status=data.GetProperty("stage").GetString(),revision=data.GetProperty("revision").GetInt64(),actions=data.GetProperty("stage").GetString()=="Ready for Delivery"&&(view.GetProperty("canFrontDesk").GetBoolean()||view.GetProperty("canManage").GetBoolean()||view.GetProperty("canReview").GetBoolean())?new[]{"publish"}:Array.Empty<string>(),galleryProvider=configuration["ClientGallery:Provider"]??"Unconfigured"});
        })).RequireAuthorization();
        app.MapPost("/api/jobs/{jobId:guid}/workflow",(Guid jobId,JobLifecycleRequest request,ClaimsPrincipal user,WorkflowStore workflow)=>WorkflowEndpoints.Run(()=>
        {
            if(request.ExpectedRevision is null)return Results.BadRequest(new{error="Refresh the Job for its current revision."});
            return Results.Ok(workflow.Execute(jobId,new(JobLifecycleRules.Canonical(request.Action),request.ExpectedRevision.Value,request.OperationId,Reason:request.Note),user));
        })).RequireAuthorization();
    }
}
