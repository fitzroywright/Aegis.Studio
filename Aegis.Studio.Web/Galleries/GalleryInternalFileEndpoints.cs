using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Common.Storage;

namespace Aegis.Studio.Web.Galleries;

public static class GalleryInternalFileEndpoints
{
    public static void MapStudioGalleryInternalFiles(this WebApplication app)
    {
        GalleryStore store = new(
            app.Services.GetRequiredService<StudioDatabase>(),
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<IFileStorage>());

        app.MapGet("/api/workspaces/{workspaceId:guid}/gallery/items/{itemId:guid}/{variant}",
            async (Guid workspaceId, Guid itemId, string variant, HttpContext context, WorkspaceAccessService access, CancellationToken cancellationToken) =>
        {
            bool allowed = context.User.IsInRole("Administrator")
                || access.HasPermission(context.User, workspaceId, "ViewAttachments")
                || access.HasPermission(context.User, workspaceId, "AdminWorkspace");
            if (!allowed) return Results.Forbid();

            WorkspaceGallery? gallery = store.Get(workspaceId);
            GalleryItem? item = gallery?.Items.FirstOrDefault(x => x.Id == itemId);
            if (gallery is null || item is null) return Results.NotFound();

            GalleryFile? file = store.ResolveFile(gallery, item, variant);
            if (file is null) return Results.NotFound();
            try
            {
                Stream content = await store.OpenFileAsync(file, cancellationToken);
                return Results.Stream(content, file.ContentType, enableRangeProcessing: true);
            }
            catch (StorageException exception) when (exception.Code == "STORAGE-MISSING-001")
            {
                return Results.NotFound();
            }
        }).RequireAuthorization();
    }
}
