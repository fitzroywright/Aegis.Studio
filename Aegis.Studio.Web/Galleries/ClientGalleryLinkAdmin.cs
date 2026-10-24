using System.Text.Json;
using Aegis.Studio.Web.Intake;
using Aegis.Studio.Web.Persistence;
using Microsoft.AspNetCore.Authorization;
using Npgsql;

namespace Aegis.Studio.Web.Galleries;

public static class ClientGalleryLinkAdmin
{
    public static void MapClientGalleryLinkAdmin(this WebApplication app)
    {
        var routes=app.MapGroup("/api/admin/gallery-client-links")
            .RequireAuthorization(new AuthorizeAttribute { Roles="Administrator" });
        routes.MapGet("/clients",(StudioDatabase database) =>
        {
            using var c=database.OpenConnection();using var q=new NpgsqlCommand("select id,display_name,email from studio_clients order by display_name",c);using var r=q.ExecuteReader();
            var items=new List<object>();while(r.Read())items.Add(new{id=r.GetGuid(0),displayName=r.GetString(1),email=r.IsDBNull(2)?"":r.GetString(2)});
            return Results.Ok(new{items});
        });
        routes.MapPost("/",async (CreateClientGalleryLink request,StudioDatabase database,IConfiguration config,CancellationToken ct) =>
        {
            if(request.ClientId==Guid.Empty)return Results.BadRequest(new{message="Choose a Studio client."});
            using(var c=database.OpenConnection())
            using(var q=new NpgsqlCommand("select exists(select 1 from studio_clients where id=$1)",c))
            {q.Parameters.AddWithValue(request.ClientId);if(!(bool)q.ExecuteScalar()!)return Results.NotFound(new{message="That Studio client ID does not exist."});}
            using var site=new PublicSiteClient(config);
            var result=await site.PostJson("/api/v1/admin/client-link-proofs",new{clientId=request.ClientId.ToString("D")},ct);
            return Results.Json(result,statusCode:StatusCodes.Status201Created);
        });
        routes.MapGet("/",async (Guid? clientId,IConfiguration config,CancellationToken ct) =>
        {
            using var site=new PublicSiteClient(config);var path="/api/v1/admin/client-link-proofs"+(clientId is Guid id?"?clientId="+Uri.EscapeDataString(id.ToString("D")):"");
            return Results.Json(await site.Get(path,ct));
        });
        routes.MapDelete("/{id:guid}",async (Guid id,IConfiguration config,CancellationToken ct) =>
        {using var site=new PublicSiteClient(config);return Results.Json(await site.DeleteJson($"/api/v1/admin/client-link-proofs/{id:D}",ct));});
    }
    public sealed record CreateClientGalleryLink(Guid ClientId);
}
