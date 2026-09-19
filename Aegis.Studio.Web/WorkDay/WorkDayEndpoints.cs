using System.Security.Claims;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Production;
using Aegis.Studio.Web.Scheduling;

namespace Aegis.Studio.Web.WorkDay;

public static class WorkDayEndpoints
{
    public static void MapStudioWorkDay(this WebApplication app)
    {
        RouteGroupBuilder group=app.MapGroup("/api/workday").RequireAuthorization();

        group.MapGet("/bootstrap",(ClaimsPrincipal user,StudioDatabase db,ScheduleStore schedules,WorkDayStore store)=>
        {
            string userName=UserName(user); PhotographerProfile? profile=new PhotographerStore(db).Get(userName);
            if(profile is null)return Results.NotFound(new{error="No Studio field profile is linked to this account."});
            DateTimeOffset now=DateTimeOffset.UtcNow; DateTimeOffset todayStart=new(now.UtcDateTime.Date,TimeSpan.Zero); DateTimeOffset todayEnd=todayStart.AddDays(1);
            WorkDayAssignment[] all=schedules.GetEntries(todayStart.AddDays(-1),todayEnd.AddDays(30))
                .Where(x=>x.ResourceIds.Contains(profile.ScheduleResourceId))
                .Select(x=>MapAssignment(x,db,store)).ToArray();
            return Results.Ok(new WorkDayBootstrap(userName,profile.DisplayName,now,
                all.Where(x=>x.StartsAtUtc<todayEnd&&x.EndsAtUtc>todayStart).ToArray(),
                all.Where(x=>x.StartsAtUtc>=todayEnd).ToArray(),
                store.GetActiveSession(userName),
                WorkDayLocationTrackingMode.SignInOutOnly,
                now));
        });

        group.MapPost("/operations",(ClaimsPrincipal user,WorkDayOperationRequest request,WorkDayStore store)=>
        {
            try
            {
                WorkDayOperationResult result=store.Apply(UserName(user),request);
                return result.State==WorkDayOperationState.Conflict?Results.Conflict(result):Results.Ok(result);
            }
            catch(InvalidOperationException ex){return Results.BadRequest(new{error=ex.Message});}
        });

        group.MapGet("/work-session",(ClaimsPrincipal user,WorkDayStore store)=>
        {
            WorkDayWorkSession? session=store.GetActiveSession(UserName(user));
            return session is null?Results.NoContent():Results.Ok(session);
        });
    }

    private static WorkDayAssignment MapAssignment(ScheduleEntry e,StudioDatabase db,WorkDayStore store)
    {
        string? customer=null,email=null,phone=null; Guid? workspace=null;
        if(e.BookingId is Guid bookingId)
        {
            using Npgsql.NpgsqlConnection c=db.OpenConnection();using Npgsql.NpgsqlCommand q=c.CreateCommand();
            q.CommandText="select client_name,client_email,client_phone,workspace_id from studio_bookings where id=$1";q.Parameters.AddWithValue(bookingId);
            using Npgsql.NpgsqlDataReader r=q.ExecuteReader();if(r.Read()){customer=r.GetString(0);email=r.GetString(1);phone=r.IsDBNull(2)?null:r.GetString(2);workspace=r.GetGuid(3);}
        }
        return new(e.Id,e.JobId,e.BookingId,e.Title,e.StartsAtUtc,e.EndsAtUtc,e.Location,e.Status,e.Notes,workspace,customer,email,phone,0);
    }

    private static string UserName(ClaimsPrincipal user)=>user.FindFirstValue("login_name")??user.Identity?.Name??string.Empty;
}
