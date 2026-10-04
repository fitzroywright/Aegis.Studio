using Aegis.Studio.Web.FrontDesk;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;

namespace Aegis.Studio.Web.Bookings;

public static class PublicBookingEndpoints
{
    public static IResult RecordDeposit(Guid id,VerifiedDepositRequest request,HttpContext context,BookingApiAuthenticator security,StudioDatabase database)
    {
        if(!security.IsAuthorized(context))return Results.Unauthorized();
        try
        {
            var confirmed=new FrontDeskBookingRequestStore(database).RecordDeposit(id,request,"Website payment confirmation");
            return Results.Ok(new{confirmed.Id,confirmed.Status,confirmed.JobId,confirmed.RecordedDeposit});
        }
        catch(ArgumentException e){return Results.BadRequest(new{error=e.Message});}
        catch(KeyNotFoundException){return Results.NotFound();}
    }
    public static IResult Create(CreateBookingIntakeRequest request,HttpContext context,BookingApiAuthenticator security,StudioDatabase database,WorkspaceStore workspaces,IConfiguration configuration)
    {
        if(!security.IsAuthorized(context))return Results.Unauthorized();
        try
        {
            if(string.IsNullOrWhiteSpace(request.ExternalBookingId))throw new ArgumentException("A stable external booking reference is required for safe retries.");
            if(request.EndsAt<=request.StartsAt || request.StartsAt==default)throw new ArgumentException("Choose a valid start and end time.");
            StudioWorkspaceDefinition? workspace=request.WorkspaceId.HasValue?workspaces.Get(request.WorkspaceId.Value):
                string.IsNullOrWhiteSpace(request.Destination)?null:workspaces.FindByRouteKey(request.Destination)??workspaces.FindByAddress(request.Destination);
            if(workspace is null||!workspace.IsEnabled)throw new ArgumentException("Choose an enabled workspace or a valid booking destination.");
            decimal deposit=configuration.GetValue<decimal?>("BookingApi:RequiredDeposit")??0;
            if(deposit<=0)return Results.Problem("Website booking intake requires a configured deposit policy. Contact FrontDesk.",statusCode:503);
            var pending=new PendingBookingRequest(workspace.Id,request.ClientName,request.ClientEmail,request.SessionType,
                DateOnly.FromDateTime(request.StartsAt.DateTime),deposit,"JMD",request.Notes,null,null,request.ExternalBookingId,
                request.StartsAt.ToUniversalTime(),request.EndsAt.ToUniversalTime(),request.Location,request.ClientPhone);
            FrontDeskRequest result=new FrontDeskBookingRequestStore(database).Create(pending,"Website booking");
            return Results.Created($"/api/public/booking-requests/{result.Id}",new{bookingRequestId=result.Id,result.Status,result.JobId,result.Request.RequiredDeposit,result.RecordedDeposit});
        }
        catch(ArgumentException e){return Results.BadRequest(new{error=e.Message});}
    }
}
