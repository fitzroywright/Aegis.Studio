using System.Security.Claims;
using Aegis.Studio.Domain.Contacts;
using Aegis.Studio.Web.Media;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Npgsql;
using Aegis.Studio.Web.Crm;
using System.Text.Json;

namespace Aegis.Studio.Web.FrontDesk;

// Contacts are independent of workspaces. Existing job client snapshots are preserved.
public sealed class StudioClientStore(StudioDatabase database)
{
    public void Migrate()
    {
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        MediaProductionStore.Execute(c,tx,"create table if not exists studio_clients(id uuid primary key,display_name text not null,email text null,phone text null,updated_at_utc timestamptz not null default now())");
        MediaProductionStore.Execute(c,tx,"alter table studio_jobs add column if not exists client_id uuid null references studio_clients(id)");
        MediaProductionStore.Execute(c,tx,"create index if not exists ix_studio_jobs_client on studio_jobs(client_id)");
        tx.Commit();
    }
    public IReadOnlyList<Contact> List(ClaimsPrincipal? user=null)
    {
        using var c=database.OpenConnection();
        var access=user is null?null:new CrmAccess(database,user);access?.Demand(CrmPermissions.View);
        using var cmd=new NpgsqlCommand(access is null?"select id,display_name,email,phone from studio_clients order by display_name":"select id,display_name,email,phone from studio_clients c where "+CrmAccess.Scope+" and c.archived_at_utc is null and c.merged_into_id is null order by display_name",c);
        if(access is not null){cmd.CommandText=cmd.CommandText.Replace("$2","$1").Replace("$3","$2");cmd.Parameters.AddWithValue(access.IsAdministrator);cmd.Parameters.AddWithValue(access.Workspaces);}
        using var r=cmd.ExecuteReader();var values=new List<Contact>();
        while(r.Read())values.Add(new(r.GetGuid(0),r.GetString(1),r.IsDBNull(2)?null:r.GetString(2),r.IsDBNull(3)?null:r.GetString(3)));return values;
    }
    public Contact Save(Guid? id,ClientRequest request)
    {
        if(string.IsNullOrWhiteSpace(request.DisplayName)||request.DisplayName.Length>200||(request.Email?.Length??0)>320||(request.Phone?.Length??0)>80)throw new ArgumentException("Enter a client name and valid contact details.");
        if(!string.IsNullOrWhiteSpace(request.Email)&&!System.Net.Mail.MailAddress.TryCreate(request.Email,out _))throw new ArgumentException("Enter a valid email address.");
        var contact=new Contact(id??Guid.NewGuid(),request.DisplayName,request.Email,request.Phone);
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        if(id.HasValue&&MediaProductionStore.Scalar(c,tx,"select id from studio_clients where id=$1",id.Value) is null)throw new KeyNotFoundException();
        MediaProductionStore.Execute(c,tx,"insert into studio_clients(id,display_name,email,phone) values($1,$2,$3,$4) on conflict(id) do update set display_name=excluded.display_name,email=excluded.email,phone=excluded.phone,updated_at_utc=now()",contact.Id,contact.DisplayName,(object?)contact.Email??DBNull.Value,(object?)contact.Phone??DBNull.Value);tx.Commit();return contact;
    }
    public void Link(Guid job,Guid? client,string user)
    {
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        if(client.HasValue&&MediaProductionStore.Scalar(c,tx,"select id from studio_clients where id=$1",client.Value) is null)throw new ArgumentException("Client not found.");
        if(MediaProductionStore.Execute(c,tx,"update studio_jobs set client_id=$2 where id=$1",job,(object?)client??DBNull.Value)!=1)throw new KeyNotFoundException();
        MediaProductionStore.Audit(c,tx,job,"ClientLinked",user,client.HasValue?"FrontDesk linked a client.":"FrontDesk removed the client link.");tx.Commit();
    }
}
public static class StudioClientEndpoints
{
    public static void MapStudioClients(this WebApplication app)
    {
        new StudioClientStore(app.Services.GetRequiredService<StudioDatabase>()).Migrate();
        var group=app.MapGroup("/api/frontdesk/clients").RequireAuthorization();
        group.MapPost("/invoices",(CombinedInvoice request,ClaimsPrincipal user,StudioDatabase db)=>Run(user,db,()=>{
            if(request.JobIds is null||request.JobIds.Length==0)throw new ArgumentException("Select jobs to invoice.");
            if(!user.IsInRole("Administrator")&&!new GlobalOperationalRoleStore(db).HasPermission(user,"Billing"))throw new UnauthorizedAccessException();
            foreach(var job in request.JobIds)if(!new JobAccessService(db).HasPermission(user,job,"RecordFinancialActivity"))throw new UnauthorizedAccessException();
            return Results.Ok(new StudioAccountsStore(db).CreateInvoice(request.JobIds[0],request.Invoice,user.FindFirstValue("login_name")??user.Identity!.Name!,request.JobIds.Skip(1).ToArray()));
        }));
        group.MapGet("",(ClaimsPrincipal user,StudioDatabase db)=>Run(user,db,()=>Results.Ok(new StudioClientStore(db).List(user))));
        group.MapPost("",(ClientRequest request,ClaimsPrincipal user,StudioDatabase db)=>Run(user,db,()=>Results.Ok(SaveCompatible(db,user,null,request))));
        group.MapPut("/{id:guid}",(Guid id,ClientRequest request,ClaimsPrincipal user,StudioDatabase db)=>Run(user,db,()=>Results.Ok(SaveCompatible(db,user,id,request))));
        group.MapPut("/job/{jobId:guid}",(Guid jobId,ClientLink request,ClaimsPrincipal user,StudioDatabase db)=>Run(user,db,()=>{new CrmAccess(db,user).Demand(CrmPermissions.Edit);if(!new JobAccessService(db).HasPermission(user,jobId,"EditJob"))throw new UnauthorizedAccessException();if(request.ClientId is {} client)new CrmAccess(db,user).Client(client);new StudioClientStore(db).Link(jobId,request.ClientId,user.FindFirstValue("login_name")??user.Identity!.Name!);return Results.NoContent();}));
    }
    private static Aegis.Studio.Domain.Contacts.Contact SaveCompatible(StudioDatabase db,ClaimsPrincipal user,Guid? id,ClientRequest request)
    {
        var crm=new CrmStore(db,user);
        var payload=id.HasValue?JsonSerializer.Deserialize<CrmClientRequest>(crm.Get(id.Value).GetRawText(),JsonSerializerOptions.Web)!:new CrmClientRequest(request.DisplayName);
        Guid saved=crm.Save(id,payload with{DisplayName=request.DisplayName,Email=request.Email,Phone=request.Phone});
        return new(saved,request.DisplayName,request.Email,request.Phone);
    }
    private static bool Allowed(ClaimsPrincipal user,StudioDatabase db)=>new CrmAccess(db,user).Has(CrmPermissions.View);
    private static IResult Run(ClaimsPrincipal user,StudioDatabase db,Func<IResult> action){if(!Allowed(user,db))return Results.Forbid();try{return action();}catch(UnauthorizedAccessException){return Results.Forbid();}catch(ArgumentException e){return Results.BadRequest(new{error=e.Message});}catch(InvalidOperationException e){return Results.BadRequest(new{error=e.Message});}catch(KeyNotFoundException){return Results.NotFound();}}
}
public sealed record ClientRequest(string DisplayName,string? Email,string? Phone);
public sealed record ClientLink(Guid? ClientId);

public sealed record CombinedInvoice(Guid[] JobIds,InvoiceRequest Invoice);
