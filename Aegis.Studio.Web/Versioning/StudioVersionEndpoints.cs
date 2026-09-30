using System.Security.Claims;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Common.Storage;

namespace Aegis.Studio.Web.Versioning;

public static class StudioVersionEndpoints
{
    public static void MapStudioVersioning(this WebApplication app)
    {
        app.MapGet("/api/jobs/{jobId:guid}/versions/{assetKey}", (Guid jobId, string assetKey, ClaimsPrincipal user, StudioDatabase db, JobAccessService access, IFileStorage storage) =>
            access.HasPermission(user, jobId, "ViewAttachments")
                ? Results.Ok(new StudioVersionStore(db, storage).GetHistory(jobId, assetKey))
                : Results.Forbid()).RequireAuthorization();

        app.MapGet("/api/jobs/{jobId:guid}/versions/by-id/{versionId:guid}/content", (Guid jobId, Guid versionId, ClaimsPrincipal user, StudioDatabase db, JobAccessService access, IFileStorage storage) =>
        {
            if (!access.HasPermission(user, jobId, "ViewAttachments")) return Results.Forbid();
            try
            {
                StudioVersionStore store = new(db, config, env);
                Stream stream = store.Open(jobId, versionId, out StudioAssetVersion version);
                return Results.File(stream, version.ContentType, version.FileName, enableRangeProcessing: true);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
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
            StudioVersionStore store = new(db, config, env);
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

