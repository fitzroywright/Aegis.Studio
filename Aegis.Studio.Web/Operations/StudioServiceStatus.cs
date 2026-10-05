using Aegis.Studio.Web.Persistence;
using Common.Storage;
using Npgsql;
namespace Aegis.Studio.Web.Operations;
public static class StudioServiceStatus
{
    public static void MapStudioServiceStatus(this WebApplication app)
    {
        app.MapGet("/api/admin/studio-status",async(StudioDatabase db,IFileStorage storage,IConfiguration configuration,CancellationToken token)=>
        {
            object storageStatus;
            try{var health=await storage.CheckHealthAsync(token).WaitAsync(TimeSpan.FromSeconds(10),token);storageStatus=new{health.Available,health.Writable,health.Provider};}
            catch(Exception error) when(error is not OperationCanceledException || !token.IsCancellationRequested){storageStatus=new{available=false,writable=false,provider="Unavailable"};}
            using var c=db.OpenConnection();using var cmd=new NpgsqlCommand("""
                select (select count(*) from studio_media_derivatives where status in ('Pending','Processing')),
                  (select count(*) from studio_media_derivatives where status='Failed'),
                  (select count(*) from studio_media_derivatives where status='Unsupported'),
                  (select count(*) from studio_outbound_email where status='Pending'),
                  (select count(*) from studio_notifications where status='Failed')
                """,c);using var r=await cmd.ExecuteReaderAsync(token);await r.ReadAsync(token);
            return Results.Ok(new{database="Available",storage=storageStatus,
                previews=new{queued=r.GetInt64(0),failed=r.GetInt64(1),unsupported=r.GetInt64(2)},
                messaging=new{queuedEmails=r.GetInt64(3),failedNotifications=r.GetInt64(4)},
                galleryConfigured=!string.IsNullOrWhiteSpace(configuration["ClientGallery:Provider"]),
                diagnostics="Common.Diagnostics",checkedAtUtc=DateTimeOffset.UtcNow});
        }).RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute{Roles="Administrator"});
    }
}
