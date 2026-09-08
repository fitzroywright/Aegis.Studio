using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;

namespace Aegis.Studio.Web.Galleries;

public static class GalleryInternalFileEndpoints
{
    public static void MapStudioGalleryInternalFiles(this WebApplication app)
    {
        GalleryStore store = new(
            app.Services.GetRequiredService<StudioDatabase>(),
            app.Services.GetRequiredService<IWebHostEnvironment>(),
            app.Services.GetRequiredService<IConfiguration>());

        app.MapGet("/api/workspaces/{workspaceId:guid}/gallery/items/{itemId:guid}/{variant}",
            (Guid workspaceId, Guid itemId, string variant, HttpContext context, WorkspaceAccessService access) =>
        {
            bool allowed = context.User.IsInRole("Administrator")
                || access.HasPermission(context.User, workspaceId, "ViewAttachments")
                || access.HasPermission(context.User, workspaceId, "AdminWorkspace");
            if (!allowed) return Results.Forbid();

            WorkspaceGallery? gallery = store.Get(workspaceId);
            GalleryItem? item = gallery?.Items.FirstOrDefault(x => x.Id == itemId);
            if (gallery is null || item is null) return Results.NotFound();

            GalleryFile? file = store.ResolveFile(gallery, item, variant);
            return file is null || !File.Exists(file.Path)
                ? Results.NotFound()
                : Results.File(file.Path, file.ContentType, enableRangeProcessing: true);
        }).RequireAuthorization();
    }
}
