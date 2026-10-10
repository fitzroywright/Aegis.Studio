using System.Security.Claims;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Production;
using Aegis.Studio.Web.Workflow;
using static Aegis.Studio.Web.Production.CommercialTemplateStore;

namespace Aegis.Studio.Web.Pos;

public sealed record PosLaunchRequest(string Action, Guid? JobId = null, Guid? BookingId = null, Guid? SaleId = null);
public sealed record PosLaunch(Guid Token, string Action, Guid? JobId, Guid? BookingId, Guid? SaleId, string Actor);

public static class FrontDeskPosLaunch
{
    public static readonly string[] Actions = ["NewSale", "TakePayment", "RecordDeposit", "IssueRefund", "ViewReceipt", "CloseRegister"];
    public static bool Matches(PosLaunch launch,string path,StudioPosStore? pos=null)
    {
        path=path.ToLowerInvariant();
        if(path.StartsWith("/api/pos/register/"))return launch.Action=="CloseRegister";
        if(path.StartsWith("/api/frontdesk/booking-requests/"))return launch.Action=="RecordDeposit"&&launch.BookingId.HasValue&&path== $"/api/frontdesk/booking-requests/{launch.BookingId:D}/deposits";
        if(path.StartsWith("/api/jobs/"))return launch.Action is "NewSale" or "TakePayment"&&launch.JobId.HasValue&&path==$"/api/jobs/{launch.JobId:D}/pos/sales";
        if(Aegis.Studio.Web.Operations.SensitiveRouteAuthorization.TryParsePosSalePath(path,out var saleId,out var operation))
        {
            if(launch.Action=="IssueRefund")return saleId==launch.SaleId&&operation is "refunds" or "refunds/hardened";
            if(launch.Action is "NewSale" or "TakePayment" && operation is "complete" or "void")
                return pos?.Get(saleId)?.ReferenceId==launch.JobId?.ToString("D");
        }
        return false;
    }
    public static void MapFrontDeskPosLaunch(this WebApplication app)
    {
        using (var c = app.Services.GetRequiredService<StudioDatabase>().OpenConnection())
            Execute(c, null, "create table if not exists studio_pos_launches(token uuid primary key,action text not null,job_id uuid null,booking_id uuid null,sale_id uuid null,actor text not null,expires_at timestamptz not null)");
        app.MapPost("/api/frontdesk/pos/launch", (PosLaunchRequest request, ClaimsPrincipal user, WorkflowStore workflow, StudioDatabase database) => CommercialEndpoints.Run(() =>
        {
            if (!workflow.IsFrontDesk(user)) return Results.Forbid();
            if (!Actions.Contains(request.Action)) throw new ArgumentException("Choose a Front Desk financial action.");
            if (request.Action is "NewSale" or "TakePayment" && request.JobId is null) throw new ArgumentException("Choose a Job.");
            if (request.Action == "RecordDeposit" && request.BookingId is null) throw new ArgumentException("Choose a booking.");
            if (request.Action is "IssueRefund" or "ViewReceipt" && request.SaleId is null) throw new ArgumentException("Choose a sale.");
            Guid? job = request.JobId;
            if (request.SaleId is Guid saleId)
            {
                var sale = app.Services.GetRequiredService<StudioPosStore>().Get(saleId) ?? throw new KeyNotFoundException();
                job = Guid.Parse(sale.ReferenceId);
            }
            var launch = new PosLaunch(Guid.NewGuid(), request.Action, job, request.BookingId, request.SaleId, WorkflowStore.Name(user));
            using var c = database.OpenConnection();
            Execute(c, null, "delete from studio_pos_launches where expires_at<now()");
            Execute(c, null, "insert into studio_pos_launches(token,action,job_id,booking_id,sale_id,actor,expires_at) values($1,$2,$3,$4,$5,$6,now()+interval '12 hours')", launch.Token, launch.Action, launch.JobId, launch.BookingId, launch.SaleId, launch.Actor);
            return Results.Ok(new { launch.Token, url = $"/pos-window.html?launch={launch.Token:D}" });
        })).RequireAuthorization();
        app.MapGet("/api/frontdesk/pos/launch/{token:guid}", (Guid token, ClaimsPrincipal user, WorkflowStore workflow, StudioDatabase database) =>
        {
            if (!workflow.IsFrontDesk(user)) return Results.Forbid();
            using var c = database.OpenConnection(); using var q = Command(c, null, "select action,job_id,booking_id,sale_id,actor from studio_pos_launches where token=$1 and actor=$2 and expires_at>now()", token, WorkflowStore.Name(user));
            using var r = q.ExecuteReader();
            return r.Read() ? Results.Ok(new PosLaunch(token, r.GetString(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.IsDBNull(3) ? null : r.GetGuid(3), r.GetString(4))) : Results.Forbid();
        }).RequireAuthorization();
        // An unguessable, actor-bound launch context is required for every POS mutation.
        app.Use(async (context, next) =>
        {
            string path = (context.Request.Path.Value ?? "").ToLowerInvariant();
            bool financialWrite = !HttpMethods.IsGet(context.Request.Method) &&
                (path.StartsWith("/api/pos/sales/") || path.StartsWith("/api/pos/register/") ||
                 (path.StartsWith("/api/jobs/") && path.EndsWith("/pos/sales")) ||
                 (path.StartsWith("/api/frontdesk/booking-requests/") && path.EndsWith("/deposits")));
            if (financialWrite)
            {
                var workflow = context.RequestServices.GetRequiredService<WorkflowStore>();
                if (context.User.Identity?.IsAuthenticated != true || !workflow.IsFrontDesk(context.User) ||
                    !Guid.TryParse(context.Request.Headers["X-Studio-Pos-Launch"], out Guid token))
                { context.Response.StatusCode = 403; return; }
                using var c = context.RequestServices.GetRequiredService<StudioDatabase>().OpenConnection();
                using var q = Command(c, null, "select action,job_id,booking_id,sale_id from studio_pos_launches where token=$1 and actor=$2 and expires_at>now()", token, WorkflowStore.Name(context.User));
                using var r=q.ExecuteReader();
                if (!r.Read()) { context.Response.StatusCode=403;return; }
                var launch=new PosLaunch(token,r.GetString(0),r.IsDBNull(1)?null:r.GetGuid(1),r.IsDBNull(2)?null:r.GetGuid(2),r.IsDBNull(3)?null:r.GetGuid(3),WorkflowStore.Name(context.User));
                r.Close();
                if(!Matches(launch,path,context.RequestServices.GetRequiredService<StudioPosStore>())){context.Response.StatusCode=403;return;}
                context.Items["StudioPosAction"]=launch.Action;
            }
            await next();
        });
    }
}
