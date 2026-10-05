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
        app.MapPost("/api/frontdesk/jobs/{jobId:guid}/fulfillment",(Guid jobId,FulfillmentRequest request,ClaimsPrincipal user,JobAccessService access,StudioDatabase db)=>
        {
            if(!access.HasPermission(user,jobId,"ChangeStatus")&&!user.IsInRole("Administrator")&&!new GlobalOperationalRoleStore(db).HasPermission(user,"Billing"))return Results.Forbid();
            try
            {
                Validate(request);string actor=user.FindFirstValue("login_name")??user.Identity?.Name??"Studio";
                using var c=db.OpenConnection();using var tx=c.BeginTransaction();
                using var job=new NpgsqlCommand("select status,conversation_id from studio_jobs where id=$1 for update",c,tx);job.Parameters.AddWithValue(jobId);
                string status;Guid conversation;
                using(var r=job.ExecuteReader()){if(!r.Read())return Results.NotFound();status=r.GetString(0);conversation=r.GetGuid(1);}
                using(var prior=new NpgsqlCommand("select job_id from studio_job_fulfillment where operation_id=$1 or job_id=$2",c,tx))
                {prior.Parameters.AddWithValue(request.OperationId);prior.Parameters.AddWithValue(jobId);if(prior.ExecuteScalar() is Guid existing){if(existing!=jobId)throw new ArgumentException("This operation belongs to another job.");return Results.Ok(new{status="Completed",alreadyApplied=true});}}
                if(status is not ("Ready for Client" or "Ready for Delivery" or "Published"))throw new ArgumentException("Production must be ready for the customer before recording collection or delivery.");
                using(var balance=new NpgsqlCommand("""
                    select coalesce(sum(case when lower(kind) in ('charge','adjustment','discount') then amount
                      when lower(kind) in ('payment','deposit','refund','reversal') then -amount else 0 end),0)
                    from studio_financial_events where job_id=$1
                    """,c,tx))
                {balance.Parameters.AddWithValue(jobId);if(Convert.ToDecimal(balance.ExecuteScalar())>0)throw new ArgumentException("A balance remains. Take payment before recording collection or delivery.");}
                MediaProductionStore.Execute(c,tx,"insert into studio_job_fulfillment(operation_id,job_id,method,recipient,recorded_by) values($1,$2,$3,$4,$5)",request.OperationId,jobId,request.Method,request.Recipient.Trim(),actor);
                MediaProductionStore.Execute(c,tx,"update studio_jobs set status='Completed' where id=$1",jobId);
                string detail=$"{request.Method} confirmed by {actor}; received by {request.Recipient.Trim()}.";
                MediaProductionStore.Audit(c,tx,jobId,"CustomerFulfillment",actor,detail);
                MediaProductionStore.Execute(c,tx,"insert into studio_messages(id,conversation_id,author_display_name,visibility,body,created_at_utc) values($1,$2,$3,'Internal',$4,now())",Guid.NewGuid(),conversation,actor,detail);
                tx.Commit();return Results.Ok(new{status="Completed"});
            }
            catch(ArgumentException error){return Results.BadRequest(new{error=error.Message});}
        }).RequireAuthorization();
    }
    public static void Validate(FulfillmentRequest request)
    {
        if(request.OperationId==Guid.Empty)throw new ArgumentException("An operation reference is required for safe retries.");
        if(request.Method is not ("Collected" or "Delivered"))throw new ArgumentException("Choose collection or delivery.");
        if(string.IsNullOrWhiteSpace(request.Recipient)||request.Recipient.Trim().Length>200)throw new ArgumentException("Record who received the completed order.");
    }
}
public sealed record FulfillmentRequest(Guid OperationId,string Method,string Recipient);
