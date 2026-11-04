using System.Security.Claims;
using Aegis.Studio.Web.Persistence;
namespace Aegis.Studio.Web.Crm;

public static class CrmEndpoints
{
    public static void MapStudioCrm(this WebApplication app)
    {
        CrmStore.Migrate(app.Services.GetRequiredService<StudioDatabase>());
        var group=app.MapGroup("/api/crm").RequireAuthorization();
        group.MapGet("/capabilities",(StudioDatabase db,ClaimsPrincipal user)=>Run(()=>Results.Ok(new CrmStore(db,user).Capabilities())));
        group.MapGet("/clients",([AsParameters] CrmQuery query,StudioDatabase db,ClaimsPrincipal user)=>Run(()=>Results.Ok(new CrmStore(db,user).Directory(query))));
        group.MapPost("/clients",(CrmClientRequest request,StudioDatabase db,ClaimsPrincipal user)=>Run(()=>Results.Ok(new{id=new CrmStore(db,user).Save(null,request)})));
        group.MapGet("/clients/{id:guid}",(Guid id,StudioDatabase db,ClaimsPrincipal user)=>Run(()=>Results.Ok(new CrmStore(db,user).Get(id))));
        group.MapPut("/clients/{id:guid}",(Guid id,CrmClientRequest request,StudioDatabase db,ClaimsPrincipal user)=>Run(()=>Results.Ok(new{id=new CrmStore(db,user).Save(id,request)})));
        group.MapPost("/clients/{id:guid}/archive",(Guid id,StudioDatabase db,ClaimsPrincipal user)=>Run(()=>{new CrmStore(db,user).Archive(id);return Results.NoContent();}));
        group.MapPost("/clients/bulk",(CrmBulkRequest request,StudioDatabase db,ClaimsPrincipal user)=>Run(()=>{new CrmStore(db,user).Bulk(request);return Results.NoContent();}));
        group.MapGet("/clients/{id:guid}/duplicates",(Guid id,StudioDatabase db,ClaimsPrincipal user)=>Run(()=>Results.Ok(new CrmStore(db,user).Duplicates(id))));
        group.MapGet("/clients/{id:guid}/merge-preview/{source:guid}",(Guid id,Guid source,StudioDatabase db,ClaimsPrincipal user)=>Run(()=>Results.Ok(new CrmStore(db,user).PreviewMerge(id,source))));
        group.MapPost("/clients/{id:guid}/merge",(Guid id,CrmMergeRequest request,StudioDatabase db,ClaimsPrincipal user)=>Run(()=>{new CrmStore(db,user).Merge(id,request);return Results.NoContent();}));
        group.MapGet("/clients/{id:guid}/contacts",(Guid id,StudioDatabase db,ClaimsPrincipal user)=>Run(()=>Results.Ok(new CrmStore(db,user).Contacts(id))));
        group.MapPost("/clients/{id:guid}/contacts",(Guid id,CrmContactRequest request,StudioDatabase db,ClaimsPrincipal user)=>Run(()=>{new CrmStore(db,user).AddContact(id,request);return Results.NoContent();}));
        group.MapDelete("/clients/{id:guid}/contacts/{relationship:guid}",(Guid id,Guid relationship,StudioDatabase db,ClaimsPrincipal user)=>Run(()=>{new CrmStore(db,user).RemoveContact(id,relationship);return Results.NoContent();}));
        group.MapPost("/clients/{id:guid}/notes",(Guid id,CrmNoteRequest request,StudioDatabase db,ClaimsPrincipal user)=>Run(()=>{new CrmStore(db,user).Note(id,request);return Results.NoContent();}));
        group.MapGet("/tasks",(Guid? clientId,bool? overdue,StudioDatabase db,ClaimsPrincipal user)=>Run(()=>Results.Ok(new CrmStore(db,user).Tasks(clientId,overdue??false))));
        group.MapPost("/clients/{id:guid}/tasks",(Guid id,CrmTaskRequest request,StudioDatabase db,ClaimsPrincipal user)=>Run(()=>Results.Ok(new{id=new CrmStore(db,user).SaveTask(id,null,request)})));
        group.MapPut("/clients/{id:guid}/tasks/{task:guid}",(Guid id,Guid task,CrmTaskRequest request,StudioDatabase db,ClaimsPrincipal user)=>Run(()=>Results.Ok(new{id=new CrmStore(db,user).SaveTask(id,task,request)})));
        group.MapPost("/clients/{id:guid}/enquiries",(Guid id,CrmEnquiryRequest request,StudioDatabase db,ClaimsPrincipal user,Aegis.Studio.Web.Operations.StudioStore studio)=>Run(()=>
        {
            var access=new CrmAccess(db,user);access.Client(id,CrmPermissions.Edit);access.Workspace(request.WorkspaceId);
            if(!new Aegis.Studio.Web.Operations.WorkspaceAccessService(db).HasPermission(user,request.WorkspaceId,"ReplyRequester"))throw new UnauthorizedAccessException();
            if(string.IsNullOrWhiteSpace(request.Subject)||request.Subject.Length>200||string.IsNullOrWhiteSpace(request.Body)||request.Body.Length>10000)throw new ArgumentException("Supply a subject and enquiry details.");
            var client=new CrmStore(db,user).Get(id);string? email=client.GetProperty("email").GetString();if(string.IsNullOrWhiteSpace(email))throw new ArgumentException("Add a client email before creating an enquiry.");
            var enquiry=studio.ReceiveInteraction(new(request.WorkspaceId,null,client.GetProperty("displayName").GetString()!,email,request.Subject,request.Body,"WalkIn"));
            using var c=db.OpenConnection();using var q=new Npgsql.NpgsqlCommand("update studio_conversations set client_id=$2 where id=$1",c);q.Parameters.AddWithValue(enquiry.Id);q.Parameters.AddWithValue(id);q.ExecuteNonQuery();return Results.Ok(new{id=enquiry.Id});
        }));
        group.MapPut("/clients/{id:guid}/enquiries/{enquiry:guid}",(Guid id,Guid enquiry,CrmLeadRequest request,StudioDatabase db,ClaimsPrincipal user)=>Run(()=>{new CrmStore(db,user).Lead(id,enquiry,request);return Results.NoContent();}));
        group.MapGet("/clients/{id:guid}/financials",(Guid id,StudioDatabase db,ClaimsPrincipal user)=>Run(()=>Results.Ok(new CrmStore(db,user).Financials(id))));
        group.MapGet("/clients/{id:guid}/sections/{section}",(Guid id,string section,int? page,StudioDatabase db,ClaimsPrincipal user)=>Run(()=>Results.Ok(new CrmStore(db,user).Section(id,section,page??1))));
        group.MapGet("/views",(StudioDatabase db,ClaimsPrincipal user)=>Run(()=>Results.Ok(new CrmStore(db,user).Views())));
        group.MapPost("/views",(CrmViewRequest request,StudioDatabase db,ClaimsPrincipal user)=>Run(()=>{new CrmStore(db,user).SaveView(request);return Results.NoContent();}));
        group.MapDelete("/views/{id:guid}",(Guid id,StudioDatabase db,ClaimsPrincipal user)=>Run(()=>{new CrmStore(db,user).DeleteView(id);return Results.NoContent();}));
        group.MapGet("/export",([AsParameters] CrmQuery query,StudioDatabase db,ClaimsPrincipal user)=>Run(()=>Results.File(System.Text.Encoding.UTF8.GetBytes(new CrmStore(db,user).Export(query)),"text/csv; charset=utf-8","studio-clients.csv")));
        group.MapPost("/import-preview",(CrmImportRequest request,StudioDatabase db,ClaimsPrincipal user)=>Run(()=>Results.Ok(new CrmStore(db,user).ImportPreview(request))));
        group.MapPost("/import",(CrmImportRequest request,StudioDatabase db,ClaimsPrincipal user)=>Run(()=>Results.Ok(new CrmStore(db,user).Import(request))));
    }
    private static IResult Run(Func<IResult> action)
    {
        try{return action();}catch(UnauthorizedAccessException){return Results.Forbid();}catch(KeyNotFoundException){return Results.NotFound();}
        catch(ArgumentException e){return Results.BadRequest(new{error=e.Message});}catch(InvalidOperationException e){return Results.Conflict(new{error=e.Message});}
        catch(Npgsql.PostgresException e) when(e.SqlState=="40001"){return Results.Conflict(new{error="Records changed concurrently. Refresh and retry."});}
    }
}
