using System.Security.Claims;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Common.Storage;
using Npgsql;

namespace Aegis.Studio.Web.Versioning;

public static class StudioVersionEndpoints
{
    public static void MapStudioVersioning(this WebApplication app)
    {
        app.MapGet("/api/jobs/{jobId:guid}/versions/{assetKey}", (Guid jobId, string assetKey, ClaimsPrincipal user, StudioDatabase db, JobAccessService access, IFileStorage storage) =>
            access.HasPermission(user, jobId, "ViewAttachments")
                ? Results.Ok(new StudioVersionStore(db, storage).GetHistory(jobId, assetKey))
                : Results.Forbid()).RequireAuthorization();

        app.MapGet("/api/jobs/{jobId:guid}/versions/by-id/{versionId:guid}/content", async (Guid jobId, Guid versionId, HttpContext context, ClaimsPrincipal user, StudioDatabase db, JobAccessService access, IFileStorage storage, IWebHostEnvironment environment) =>
        {
            if (!access.HasPermission(user, jobId, "ViewAttachments")) return Results.Forbid();
            try
            {
                StudioVersionStore store = new(db, storage);
                StudioAssetVersion version = store.Get(jobId, versionId) ?? throw new KeyNotFoundException("Version was not found.");
                Stream stream = await new StoredVersionRecovery(storage, environment).OpenAsync(version, context.RequestAborted);
                return Results.File(stream, version.ContentType, version.FileName, enableRangeProcessing: true);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        }).RequireAuthorization();

        app.MapGet("/api/jobs/{jobId:guid}/versions/by-id/{versionId:guid}/preview", async
            (Guid jobId,Guid versionId,string? size,HttpContext context,ClaimsPrincipal user,StudioDatabase db,JobAccessService access,IFileStorage storage) =>
        {
            if(!access.HasPermission(user,jobId,"ViewAttachments"))return Results.Forbid();
            if(size is not null && size is not "thumbnail" and not "preview")return Results.BadRequest(new{error="Choose thumbnail or preview."});
            using var c=db.OpenConnection();
            using var cmd=new NpgsqlCommand("select d.status,d.thumbnail_key,d.preview_key,v.content_type,v.stored_path,v.note from studio_asset_versions v left join studio_media_derivatives d on d.version_id=v.id where v.job_id=$1 and v.id=$2",c);
            cmd.Parameters.AddWithValue(jobId);cmd.Parameters.AddWithValue(versionId);
            string state;string? key;string contentType;string storedPath;string? note;
            using(var r=await cmd.ExecuteReaderAsync(context.RequestAborted))
            {
                if(!await r.ReadAsync(context.RequestAborted))return Results.NotFound();
                state=r.IsDBNull(0)?"Pending":r.GetString(0);
                int index=size=="thumbnail"?1:2;
                key=r.IsDBNull(index)?null:r.GetString(index);
                contentType=r.GetString(3);
                storedPath=r.GetString(4);
                note=r.IsDBNull(5)?null:r.GetString(5);
            }
            context.Response.Headers.CacheControl="private, no-cache";
            if(state!="Ready"||key is null)
            {
                bool seededDemoImage=contentType.StartsWith("image/",StringComparison.OrdinalIgnoreCase)
                    && (note?.StartsWith("Real CC0/public-domain photograph from Wikimedia Commons.",StringComparison.Ordinal)==true
                        || note?.StartsWith("CC0 wedding photograph from Wikimedia Commons, seeded for Studio demonstration data.",StringComparison.Ordinal)==true);
                if(seededDemoImage)
                {
                    try
                    {
                        var versions=new PrefixedFileStorage(storage,"VersionStore");
                        return Results.File(await versions.OpenReadAsync(storedPath,context.RequestAborted),contentType,enableRangeProcessing:true);
                    }
                    catch(StorageException e) when(e.Code=="STORAGE-MISSING-001")
                    {
                        // Keep the normal retry response below. The derivative worker will
                        // attempt StoredVersionRecovery for a recoverable seeded original.
                    }
                }
                context.Response.Headers.RetryAfter="10";
                return Results.Json(new{error="Preview not available. Download the original or retry the preview.",state},statusCode:state=="Unsupported"?415:503);
            }
            try{return Results.File(await storage.OpenReadAsync(key,context.RequestAborted),"image/jpeg",enableRangeProcessing:true);}
            catch(StorageException e) when(e.Code=="STORAGE-MISSING-001")
            {
                using var retry=new NpgsqlCommand("update studio_media_derivatives set status='Pending',next_attempt_at=now() where version_id=$1",c);retry.Parameters.AddWithValue(versionId);await retry.ExecuteNonQueryAsync(context.RequestAborted);
                return Results.Json(new{error="Preview file unavailable; regeneration queued."},statusCode:503);
            }
        }).RequireAuthorization();
        app.MapPost("/api/jobs/{jobId:guid}/versions/by-id/{versionId:guid}/preview/retry", async
            (Guid jobId,Guid versionId,ClaimsPrincipal user,StudioDatabase db,JobAccessService access,CancellationToken token) =>
        {
            if(!access.HasPermission(user,jobId,"ManageProduction"))return Results.Forbid();
            using var c=db.OpenConnection();using var cmd=new NpgsqlCommand("""
                insert into studio_media_derivatives(version_id) select id from studio_asset_versions where job_id=$1 and id=$2
                on conflict(version_id) do update set status='Pending',attempts=0,next_attempt_at=now(),last_error=null
                where studio_media_derivatives.status<>'Processing' or studio_media_derivatives.next_attempt_at<now()
                """,c);cmd.Parameters.AddWithValue(jobId);cmd.Parameters.AddWithValue(versionId);
            await cmd.ExecuteNonQueryAsync(token);return Results.Accepted();
        }).RequireAuthorization();

        app.MapPost("/api/jobs/{jobId:guid}/versions/{assetKey}", async (Guid jobId, string assetKey, HttpRequest request, ClaimsPrincipal user, StudioDatabase db, JobAccessService access, IFileStorage storage) =>
        {
            if (!access.HasPermission(user, jobId, "ManageProduction")) return Results.Forbid();
            if (!request.HasFormContentType) return Results.BadRequest(new { error = "multipart/form-data is required." });
            IFormCollection form = await request.ReadFormAsync();
            IFormFile? file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0) return Results.BadRequest(new { error = "A file is required." });
            await using Stream input = file.OpenReadStream();
            StudioAssetVersion value = new StudioVersionStore(db, storage).Add(jobId, assetKey, file.FileName, file.ContentType, input, UserName(user), form["note"].FirstOrDefault());
            return Results.Created($"/api/jobs/{jobId}/versions/by-id/{value.Id}", value);
        }).DisableAntiforgery().RequireAuthorization();

        app.MapPost("/api/jobs/{jobId:guid}/versions/by-id/{versionId:guid}/restore", (Guid jobId, Guid versionId, RestoreVersionRequest request, ClaimsPrincipal user, StudioDatabase db, JobAccessService access, IFileStorage storage) =>
        {
            if (!access.HasPermission(user, jobId, "ManageProduction")) return Results.Forbid();
            try
            {
                return Results.Ok(new StudioVersionStore(db, storage).Restore(jobId, versionId, UserName(user), request.Note));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        }).RequireAuthorization();

        app.MapGet("/api/jobs/{jobId:guid}/versions/compare", (Guid jobId, Guid left, Guid right, ClaimsPrincipal user, StudioDatabase db, JobAccessService access, IFileStorage storage) =>
        {
            if (!access.HasPermission(user, jobId, "ViewAttachments")) return Results.Forbid();
            StudioVersionStore store = new(db, storage);
            StudioAssetVersion? a = store.Get(jobId, left);
            StudioAssetVersion? b = store.Get(jobId, right);
            if (a is null || b is null) return Results.NotFound();
            return Results.Ok(new
            {
                left = a,
                right = b,
                sameContent = string.Equals(a.Sha256, b.Sha256, StringComparison.OrdinalIgnoreCase),
                sizeDelta = b.SizeBytes - a.SizeBytes
            });
        }).RequireAuthorization();
    }

    private static string UserName(ClaimsPrincipal user) => user.FindFirst("login_name")?.Value ?? user.Identity?.Name ?? "Studio";
}

public sealed record RestoreVersionRequest(string? Note);

