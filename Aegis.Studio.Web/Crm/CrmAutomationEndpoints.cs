using System.Security.Claims;
using System.Text.Json;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Npgsql;
namespace Aegis.Studio.Web.Crm;
public sealed record CrmAutomationRequest(string Name,Guid WorkspaceId,string TriggerEvent,JsonElement[] Actions,bool Enabled=true);
public static class CrmAutomationEndpoints
{
    public static readonly string[] Triggers=["ClientCreated","EnquiryReceived","LeadStatusChanged","QuoteSent","BookingConfirmed","SessionApproaching","SessionCompleted","GalleryDelivered","PaymentOverdue","JobCompleted","ClientInactive"];
    public static void MapStudioCrmAutomations(this WebApplication app)
    {
        var group=app.MapGroup("/api/crm/automations").RequireAuthorization();
        group.MapGet("",(StudioDatabase db,ClaimsPrincipal user)=>Run(()=>
        {
            var access=new CrmAccess(db,user);access.Demand(CrmPermissions.Automations);
            return Results.Ok(WorkflowAutomationRuntime.Definitions.Where(d=>Triggers.Contains(d.TriggerEvent)&&JsonDocument.Parse(d.ConditionsJson).RootElement.TryGetProperty("workspaceId",out var w)&&Guid.TryParse(w.GetString(),out var id)&&(access.IsAdministrator||access.Workspaces.Contains(id))));
        }));
        group.MapPost("",(CrmAutomationRequest request,StudioDatabase db,ClaimsPrincipal user)=>Run(()=>
        {
            var access=new CrmAccess(db,user);access.Demand(CrmPermissions.Automations);access.Workspace(request.WorkspaceId);
            if(!Triggers.Contains(request.TriggerEvent)||request.Actions is null||request.Actions.Length is <1 or >20||request.Actions.Any(a=>!a.TryGetProperty("type",out var type)||!CrmAutomation.Actions.Contains(type.GetString()??"")))throw new ArgumentException("Choose a supported CRM trigger and actions.");
            var saved=WorkflowAutomationRuntime.SaveCrmDefinition(new(request.Name,"Workspace CRM automation",request.TriggerEvent,JsonSerializer.Serialize(new{workspaceId=request.WorkspaceId}),JsonSerializer.Serialize(request.Actions),request.Enabled));
            return Results.Ok(saved);
        }));
        group.MapPut("/workspaces/{id:guid}",(Guid id,CrmAutomationWorkspaceRequest request,StudioDatabase db,ClaimsPrincipal user)=>Run(()=>
        {
            var access=new CrmAccess(db,user);access.Demand(CrmPermissions.Automations);access.Workspace(id);
            using var c=db.OpenConnection();using var tx=c.BeginTransaction();using(var q=new NpgsqlCommand("update studio_workspaces set crm_automation_enabled=$2 where id=$1",c,tx)){q.Parameters.AddWithValue(id);q.Parameters.AddWithValue(request.Enabled);if(q.ExecuteNonQuery()!=1)throw new KeyNotFoundException();}
            using(var audit=new NpgsqlCommand("insert into studio_workflow_configuration_audit(id,action,actor,detail) values(gen_random_uuid(),'CrmAutomationWorkspace',$1,$2::jsonb)",c,tx)){audit.Parameters.AddWithValue(CrmAccess.Name(user));audit.Parameters.AddWithValue(JsonSerializer.Serialize(new{workspaceId=id,request.Enabled}));audit.ExecuteNonQuery();}tx.Commit();return Results.NoContent();
        }));
        group.MapDelete("/{id:guid}",(Guid id,StudioDatabase db,ClaimsPrincipal user)=>Run(()=>
        {
            var access=new CrmAccess(db,user);access.Demand(CrmPermissions.Automations);var definition=WorkflowAutomationRuntime.Definitions.SingleOrDefault(d=>d.Id==id)??throw new KeyNotFoundException();
            if(JsonDocument.Parse(definition.ActionsJson).RootElement.EnumerateArray().Any(a=>!a.TryGetProperty("type",out var type)||!CrmAutomation.Actions.Contains(type.GetString()??""))||!Triggers.Contains(definition.TriggerEvent)||!JsonDocument.Parse(definition.ConditionsJson).RootElement.TryGetProperty("workspaceId",out var workspace)||!Guid.TryParse(workspace.GetString(),out var scope))throw new UnauthorizedAccessException();access.Workspace(scope);WorkflowAutomationRuntime.DisableCrmDefinition(definition);return Results.NoContent();
        }));
    }
    private static IResult Run(Func<IResult> fn){try{return fn();}catch(UnauthorizedAccessException){return Results.Forbid();}catch(KeyNotFoundException){return Results.NotFound();}catch(ArgumentException e){return Results.BadRequest(new{error=e.Message});}catch(InvalidOperationException e){return Results.BadRequest(new{error=e.Message});}}
}
public sealed record CrmAutomationWorkspaceRequest(bool Enabled);
