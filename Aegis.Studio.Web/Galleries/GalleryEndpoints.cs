using System.Security.Cryptography;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Npgsql;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace Aegis.Studio.Web.Galleries;

public static class GalleryEndpoints
{
    public static void MapStudioGalleries(this WebApplication app)
    {
        GalleryStore store = new(
            app.Services.GetRequiredService<StudioDatabase>(),
            app.Services.GetRequiredService<IWebHostEnvironment>(),
            app.Services.GetRequiredService<IConfiguration>());
        store.EnsureSchema();

        app.MapGet("/api/workspaces/{workspaceId:guid}/gallery", (Guid workspaceId, HttpContext context, WorkspaceAccessService access) =>
        {
            if (!CanView(access, context, workspaceId)) return Results.Forbid();
            return Results.Ok(store.GetOrCreate(workspaceId));
        }).RequireAuthorization();

        app.MapPost("/api/workspaces/{workspaceId:guid}/gallery/items", async (Guid workspaceId, HttpContext context, WorkspaceAccessService access, StudioOperationsFlowPublisher operationsFlows, CancellationToken cancellationToken) =>
        {
            if (!CanManage(access, context, workspaceId)) return Results.Forbid();
            if (!context.Request.HasFormContentType) return Results.BadRequest(new { error = "multipart/form-data is required." });
            IFormCollection form = await context.Request.ReadFormAsync(cancellationToken);
            if (form.Files.Count == 0) return Results.BadRequest(new { error = "Select at least one image." });
            try
            {
                WorkspaceGallery gallery = store.GetOrCreate(workspaceId);
                List<GalleryItem> added = [];
                foreach (IFormFile file in form.Files) added.Add(await store.AddImageAsync(gallery, file, cancellationToken));
                operationsFlows.TryEnqueueGallery(workspaceId.ToString("D"), "InternalStorage", businessId: workspaceId.ToString("D"));
                return Results.Ok(new { gallery = store.Get(workspaceId), added });
            }
            catch (InvalidOperationException exception)
            {
                operationsFlows.TryEnqueueGallery(workspaceId.ToString("D"), "InternalStorage", businessId: workspaceId.ToString("D"), detail: $"Gallery storage failed ({exception.GetType().Name}).", failed: true);
                return Results.BadRequest(new { error = exception.Message });
            }
        }).RequireAuthorization().DisableAntiforgery();

        app.MapPut("/api/workspaces/{workspaceId:guid}/gallery", (Guid workspaceId, GalleryUpdateRequest request, HttpContext context, WorkspaceAccessService access) =>
        {
            if (!CanManage(access, context, workspaceId)) return Results.Forbid();
            return Results.Ok(store.UpdateGallery(workspaceId, request));
        }).RequireAuthorization();

        app.MapPut("/api/workspaces/{workspaceId:guid}/gallery/items/{itemId:guid}", (Guid workspaceId, Guid itemId, GalleryItemUpdateRequest request, HttpContext context, WorkspaceAccessService access) =>
        {
            if (!CanManage(access, context, workspaceId)) return Results.Forbid();
            try { return Results.Ok(store.UpdateItem(workspaceId, itemId, request)); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        }).RequireAuthorization();

        app.MapDelete("/api/workspaces/{workspaceId:guid}/gallery/items/{itemId:guid}", (Guid workspaceId, Guid itemId, HttpContext context, WorkspaceAccessService access) =>
        {
            if (!CanManage(access, context, workspaceId)) return Results.Forbid();
            store.DeleteItem(workspaceId, itemId);
            return Results.NoContent();
        }).RequireAuthorization();

        app.MapPost("/api/workspaces/{workspaceId:guid}/gallery/publish", (Guid workspaceId, HttpContext context, WorkspaceAccessService access, StudioOperationsFlowPublisher operationsFlows) =>
        {
            if (!CanPublish(access, context, workspaceId)) return Results.Forbid();
            try
            {
                string correlationId = workspaceId.ToString("D");
                operationsFlows.TryEnqueueGallery(correlationId, "Publish", businessId: workspaceId.ToString("D"));
                WorkspaceGallery gallery = store.Publish(workspaceId);
                operationsFlows.TryEnqueueGallery(correlationId, "PublicGallery", businessId: gallery.Id.ToString("D"));

                WorkspaceGallery? verified = store.GetPublished(gallery.PublicToken);
                if (verified is null || !verified.IsPublished || !verified.Items.Any(x => x.IsVisible))
                {
                    operationsFlows.TryEnqueueGallery(
                        correlationId,
                        "Verification",
                        businessId: gallery.Id.ToString("D"),
                        detail: "PublishedGalleryReadbackFailed",
                        failed: true);
                    return Results.Problem("The gallery was published but verification failed.", statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                operationsFlows.TryEnqueueGallery(correlationId, "Verification", businessId: gallery.Id.ToString("D"));
                string publicUrl = $"{context.Request.Scheme}://{context.Request.Host}/g/{gallery.PublicToken}";
                return Results.Ok(new { gallery = verified, publicUrl });
            }
            catch (Exception exception)
            {
                operationsFlows.TryEnqueueGallery(workspaceId.ToString("D"), "Publish", businessId: workspaceId.ToString("D"), detail: $"Gallery publication failed ({exception.GetType().Name}).", failed: true);
                throw;
            }
        }).RequireAuthorization();

        app.MapPost("/api/workspaces/{workspaceId:guid}/gallery/unpublish", (Guid workspaceId, HttpContext context, WorkspaceAccessService access) =>
        {
            if (!CanPublish(access, context, workspaceId)) return Results.Forbid();
            return Results.Ok(store.Unpublish(workspaceId));
        }).RequireAuthorization();

        app.MapGet("/g/{token}", (string token, IWebHostEnvironment environment) =>
            store.GetPublished(token) is null
                ? Results.NotFound()
                : Results.File(Path.Combine(environment.WebRootPath, "public-gallery.html"), "text/html; charset=utf-8")).AllowAnonymous();

        app.MapGet("/public/galleries/{token}", (string token, HttpContext context) =>
        {
            WorkspaceGallery? gallery = store.GetPublished(token);
            if (gallery is null) return Results.NotFound();
            return Results.Ok(new
            {
                gallery.Id,
                gallery.WorkspaceId,
                gallery.Title,
                gallery.Description,
                gallery.PublishedAtUtc,
                items = gallery.Items.Where(x => x.IsVisible).OrderBy(x => x.SortOrder).ThenBy(x => x.CreatedAtUtc).Select(x => new
                {
                    x.Id,
                    x.FileName,
                    x.Caption,
                    x.SortOrder,
                    thumbnailUrl = $"/public/galleries/{token}/items/{x.Id}/thumbnail",
                    previewUrl = $"/public/galleries/{token}/items/{x.Id}/preview",
                    originalUrl = $"/public/galleries/{token}/items/{x.Id}/original"
                })
            });
        }).AllowAnonymous();

        app.MapGet("/public/galleries/{token}/items/{itemId:guid}/{variant}", (string token, Guid itemId, string variant) =>
        {
            WorkspaceGallery? gallery = store.GetPublished(token);
            GalleryItem? item = gallery?.Items.FirstOrDefault(x => x.Id == itemId && x.IsVisible);
            if (gallery is null || item is null) return Results.NotFound();
            GalleryFile? file = store.ResolveFile(gallery, item, variant);
            return file is null || !File.Exists(file.Path)
                ? Results.NotFound()
                : Results.File(file.Path, file.ContentType, enableRangeProcessing: true);
        }).AllowAnonymous();
    }

    private static bool CanView(WorkspaceAccessService access, HttpContext context, Guid workspaceId)
        => context.User.IsInRole("Administrator") || access.HasPermission(context.User, workspaceId, "ViewAttachments") || access.HasPermission(context.User, workspaceId, "AdminWorkspace");

    private static bool CanManage(WorkspaceAccessService access, HttpContext context, Guid workspaceId)
        => context.User.IsInRole("Administrator") || access.HasPermission(context.User, workspaceId, "AddAttachments") || access.HasPermission(context.User, workspaceId, "ManageProduction") || access.HasPermission(context.User, workspaceId, "AdminWorkspace");

    private static bool CanPublish(WorkspaceAccessService access, HttpContext context, Guid workspaceId)
        => context.User.IsInRole("Administrator") || access.HasPermission(context.User, workspaceId, "EditWorkspace") || access.HasPermission(context.User, workspaceId, "AdminWorkspace");
}

public sealed class GalleryStore
{
    private readonly StudioDatabase database;
    private readonly string root;
    private readonly long maximumBytes;

    public GalleryStore(StudioDatabase database, IWebHostEnvironment environment, IConfiguration configuration)
    {
        this.database = database;
        string configured = configuration["Storage:GalleriesPath"] ?? string.Empty;
        root = string.IsNullOrWhiteSpace(configured) ? Path.Combine(environment.ContentRootPath, "Storage", "galleries") : Path.GetFullPath(configured);
        maximumBytes = Math.Clamp(configuration.GetValue<int?>("Storage:MaxGalleryImageMegabytes") ?? 50, 1, 250) * 1024L * 1024L;
        Directory.CreateDirectory(root);
    }

    public void EnsureSchema()
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            create table if not exists studio_workspace_galleries
            (
                id uuid primary key,
                workspace_id uuid not null unique references studio_workspaces(id) on delete cascade,
                title text not null,
                description text null,
                public_token text not null unique,
                is_published boolean not null default false,
                published_at_utc timestamptz null,
                created_at_utc timestamptz not null,
                updated_at_utc timestamptz not null
            );
            create table if not exists studio_gallery_items
            (
                id uuid primary key,
                gallery_id uuid not null references studio_workspace_galleries(id) on delete cascade,
                file_name text not null,
                stored_name text not null,
                content_type text not null,
                size_bytes bigint not null,
                sha256 text not null,
                caption text null,
                sort_order integer not null default 0,
                is_visible boolean not null default true,
                created_at_utc timestamptz not null
            );
            create index if not exists ix_studio_gallery_items_gallery_sort on studio_gallery_items(gallery_id,sort_order,created_at_utc);
            """;
        command.ExecuteNonQuery();
    }

    public WorkspaceGallery GetOrCreate(Guid workspaceId)
    {
        WorkspaceGallery? current = Get(workspaceId);
        if (current is not null) return current;
        string workspaceName = GetWorkspaceName(workspaceId) ?? throw new KeyNotFoundException("Workspace was not found.");
        Guid id = Guid.NewGuid();
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "insert into studio_workspace_galleries(id,workspace_id,title,description,public_token,is_published,published_at_utc,created_at_utc,updated_at_utc) values($1,$2,$3,null,$4,false,null,now(),now()) on conflict(workspace_id) do nothing";
        command.Parameters.AddWithValue(id); command.Parameters.AddWithValue(workspaceId); command.Parameters.AddWithValue(workspaceName); command.Parameters.AddWithValue(token); command.ExecuteNonQuery();
        return Get(workspaceId)!;
    }

    public WorkspaceGallery? Get(Guid workspaceId)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id,workspace_id,title,description,public_token,is_published,published_at_utc,created_at_utc,updated_at_utc from studio_workspace_galleries where workspace_id=$1";
        command.Parameters.AddWithValue(workspaceId);
        using NpgsqlDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        Guid id = reader.GetGuid(0);
        WorkspaceGallery gallery = ReadGallery(reader, []);
        reader.Close();
        return gallery with { Items = GetItems(id) };
    }

    public WorkspaceGallery? GetPublished(string token)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id,workspace_id,title,description,public_token,is_published,published_at_utc,created_at_utc,updated_at_utc from studio_workspace_galleries where public_token=$1 and is_published";
        command.Parameters.AddWithValue(token);
        using NpgsqlDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        Guid id = reader.GetGuid(0);
        WorkspaceGallery gallery = ReadGallery(reader, []);
        reader.Close();
        return gallery with { Items = GetItems(id) };
    }

    public async Task<GalleryItem> AddImageAsync(WorkspaceGallery gallery, IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length <= 0 || file.Length > maximumBytes) throw new InvalidOperationException($"Gallery images must be between 1 byte and {maximumBytes / 1024L / 1024L} MB.");
        if (string.IsNullOrWhiteSpace(file.ContentType) || !file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"{file.FileName} is not an image.");
        string fileName = Path.GetFileName(file.FileName);
        Guid itemId = Guid.NewGuid();
        string extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(extension)) extension = ".img";
        string storedName = itemId.ToString("N") + extension;
        string folder = GalleryDirectory(gallery);
        Directory.CreateDirectory(Path.Combine(folder, "originals"));
        Directory.CreateDirectory(Path.Combine(folder, "previews"));
        Directory.CreateDirectory(Path.Combine(folder, "thumbnails"));
        string original = Path.Combine(folder, "originals", storedName);
        string preview = Path.Combine(folder, "previews", itemId.ToString("N") + ".jpg");
        string thumbnail = Path.Combine(folder, "thumbnails", itemId.ToString("N") + ".jpg");
        string temporary = original + ".uploading";
        try
        {
            await using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous)) await file.CopyToAsync(output, cancellationToken);
            await using FileStream verify = File.OpenRead(temporary);
            byte[] sha = await SHA256.HashDataAsync(verify, cancellationToken);
            verify.Position = 0;
            using Image image = await Image.LoadAsync(temporary, cancellationToken);
            using Image previewImage = image.Clone(x => x.AutoOrient().Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(1600, 1600) }));
            using Image thumbnailImage = image.Clone(x => x.AutoOrient().Resize(new ResizeOptions { Mode = ResizeMode.Crop, Size = new Size(360, 240) }));
            await previewImage.SaveAsJpegAsync(preview, new JpegEncoder { Quality = 86 }, cancellationToken);
            await thumbnailImage.SaveAsJpegAsync(thumbnail, new JpegEncoder { Quality = 80 }, cancellationToken);
            File.Move(temporary, original);
            int order = NextSortOrder(gallery.Id);
            using NpgsqlConnection connection = database.OpenConnection();
            using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = "insert into studio_gallery_items(id,gallery_id,file_name,stored_name,content_type,size_bytes,sha256,caption,sort_order,is_visible,created_at_utc) values($1,$2,$3,$4,$5,$6,$7,null,$8,true,now())";
            command.Parameters.AddWithValue(itemId); command.Parameters.AddWithValue(gallery.Id); command.Parameters.AddWithValue(fileName); command.Parameters.AddWithValue(storedName); command.Parameters.AddWithValue(file.ContentType); command.Parameters.AddWithValue(file.Length); command.Parameters.AddWithValue(Convert.ToHexString(sha).ToLowerInvariant()); command.Parameters.AddWithValue(order); command.ExecuteNonQuery();
            return GetItems(gallery.Id).First(x => x.Id == itemId);
        }
        catch
        {
            foreach (string path in new[] { temporary, original, preview, thumbnail }) if (File.Exists(path)) File.Delete(path);
            throw;
        }
    }

    public WorkspaceGallery UpdateGallery(Guid workspaceId, GalleryUpdateRequest request)
    {
        WorkspaceGallery gallery = GetOrCreate(workspaceId);
        string title = string.IsNullOrWhiteSpace(request.Title) ? gallery.Title : request.Title.Trim();
        using NpgsqlConnection connection = database.OpenConnection(); using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "update studio_workspace_galleries set title=$2,description=$3,updated_at_utc=now() where workspace_id=$1";
        command.Parameters.AddWithValue(workspaceId); command.Parameters.AddWithValue(title); command.Parameters.AddWithValue((object?)request.Description?.Trim() ?? DBNull.Value); command.ExecuteNonQuery();
        return Get(workspaceId)!;
    }

    public GalleryItem UpdateItem(Guid workspaceId, Guid itemId, GalleryItemUpdateRequest request)
    {
        WorkspaceGallery gallery = GetOrCreate(workspaceId);
        using NpgsqlConnection connection = database.OpenConnection(); using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "update studio_gallery_items set caption=$3,sort_order=$4,is_visible=$5 where id=$1 and gallery_id=$2";
        command.Parameters.AddWithValue(itemId); command.Parameters.AddWithValue(gallery.Id); command.Parameters.AddWithValue((object?)request.Caption?.Trim() ?? DBNull.Value); command.Parameters.AddWithValue(request.SortOrder); command.Parameters.AddWithValue(request.IsVisible); if (command.ExecuteNonQuery() == 0) throw new KeyNotFoundException();
        return GetItems(gallery.Id).First(x => x.Id == itemId);
    }

    public void DeleteItem(Guid workspaceId, Guid itemId)
    {
        WorkspaceGallery gallery = GetOrCreate(workspaceId);
        GalleryItem? item = gallery.Items.FirstOrDefault(x => x.Id == itemId);
        if (item is null) return;
        using NpgsqlConnection connection = database.OpenConnection(); using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "delete from studio_gallery_items where id=$1 and gallery_id=$2"; command.Parameters.AddWithValue(itemId); command.Parameters.AddWithValue(gallery.Id); command.ExecuteNonQuery();
        foreach (string variant in new[] { "original", "preview", "thumbnail" }) { GalleryFile? file = ResolveFile(gallery, item, variant); if (file is not null && File.Exists(file.Path)) File.Delete(file.Path); }
    }

    public WorkspaceGallery Publish(Guid workspaceId)
    {
        WorkspaceGallery gallery = GetOrCreate(workspaceId);
        if (!gallery.Items.Any(x => x.IsVisible)) throw new InvalidOperationException("Add at least one visible image before publishing.");
        using NpgsqlConnection connection = database.OpenConnection(); using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "update studio_workspace_galleries set is_published=true,published_at_utc=now(),updated_at_utc=now() where workspace_id=$1"; command.Parameters.AddWithValue(workspaceId); command.ExecuteNonQuery();
        WriteManifest(Get(workspaceId)!);
        return Get(workspaceId)!;
    }

    public WorkspaceGallery Unpublish(Guid workspaceId)
    {
        GetOrCreate(workspaceId);
        using NpgsqlConnection connection = database.OpenConnection(); using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "update studio_workspace_galleries set is_published=false,updated_at_utc=now() where workspace_id=$1"; command.Parameters.AddWithValue(workspaceId); command.ExecuteNonQuery();
        return Get(workspaceId)!;
    }

    public GalleryFile? ResolveFile(WorkspaceGallery gallery, GalleryItem item, string variant)
    {
        string folder = GalleryDirectory(gallery);
        return variant.ToLowerInvariant() switch
        {
            "original" => new GalleryFile(SafePath(Path.Combine(folder, "originals", item.StoredName)), item.ContentType),
            "preview" => new GalleryFile(SafePath(Path.Combine(folder, "previews", item.Id.ToString("N") + ".jpg")), "image/jpeg"),
            "thumbnail" => new GalleryFile(SafePath(Path.Combine(folder, "thumbnails", item.Id.ToString("N") + ".jpg")), "image/jpeg"),
            _ => null
        };
    }

    private IReadOnlyList<GalleryItem> GetItems(Guid galleryId)
    {
        using NpgsqlConnection connection = database.OpenConnection(); using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id,gallery_id,file_name,stored_name,content_type,size_bytes,sha256,caption,sort_order,is_visible,created_at_utc from studio_gallery_items where gallery_id=$1 order by sort_order,created_at_utc"; command.Parameters.AddWithValue(galleryId);
        using NpgsqlDataReader reader = command.ExecuteReader(); List<GalleryItem> values = []; while (reader.Read()) values.Add(new GalleryItem(reader.GetGuid(0),reader.GetGuid(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetInt64(5),reader.GetString(6),reader.IsDBNull(7)?null:reader.GetString(7),reader.GetInt32(8),reader.GetBoolean(9),reader.GetFieldValue<DateTimeOffset>(10))); return values;
    }

    private int NextSortOrder(Guid galleryId)
    {
        using NpgsqlConnection connection = database.OpenConnection(); using NpgsqlCommand command = connection.CreateCommand(); command.CommandText="select coalesce(max(sort_order),-1)+1 from studio_gallery_items where gallery_id=$1"; command.Parameters.AddWithValue(galleryId); return Convert.ToInt32(command.ExecuteScalar());
    }

    private string? GetWorkspaceName(Guid workspaceId)
    {
        using NpgsqlConnection connection=database.OpenConnection(); using NpgsqlCommand command=connection.CreateCommand(); command.CommandText="select name from studio_workspaces where id=$1"; command.Parameters.AddWithValue(workspaceId); return command.ExecuteScalar() as string;
    }

    private string GalleryDirectory(WorkspaceGallery gallery) => SafePath(Path.Combine(root, gallery.WorkspaceId.ToString("N"), gallery.Id.ToString("N")));
    private string SafePath(string path) { string full=Path.GetFullPath(path); string safeRoot=Path.GetFullPath(root)+Path.DirectorySeparatorChar; if(!full.StartsWith(safeRoot,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Gallery path escaped storage root."); return full; }
    private void WriteManifest(WorkspaceGallery gallery) { string folder=GalleryDirectory(gallery); Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder,"published.json"), System.Text.Json.JsonSerializer.Serialize(new { gallery.Id,gallery.WorkspaceId,gallery.Title,gallery.Description,gallery.PublicToken,gallery.PublishedAtUtc,items=gallery.Items.Where(x=>x.IsVisible).OrderBy(x=>x.SortOrder).Select(x=>new{x.Id,x.FileName,x.Caption,x.SortOrder,x.Sha256}) }, new System.Text.Json.JsonSerializerOptions { WriteIndented=true })); }
    private static WorkspaceGallery ReadGallery(NpgsqlDataReader reader,IReadOnlyList<GalleryItem> items) => new(reader.GetGuid(0),reader.GetGuid(1),reader.GetString(2),reader.IsDBNull(3)?null:reader.GetString(3),reader.GetString(4),reader.GetBoolean(5),reader.IsDBNull(6)?null:reader.GetFieldValue<DateTimeOffset>(6),reader.GetFieldValue<DateTimeOffset>(7),reader.GetFieldValue<DateTimeOffset>(8),items);
}

public sealed record WorkspaceGallery(Guid Id,Guid WorkspaceId,string Title,string? Description,string PublicToken,bool IsPublished,DateTimeOffset? PublishedAtUtc,DateTimeOffset CreatedAtUtc,DateTimeOffset UpdatedAtUtc,IReadOnlyList<GalleryItem> Items);
public sealed record GalleryItem(Guid Id,Guid GalleryId,string FileName,string StoredName,string ContentType,long SizeBytes,string Sha256,string? Caption,int SortOrder,bool IsVisible,DateTimeOffset CreatedAtUtc);
public sealed record GalleryUpdateRequest(string? Title,string? Description);
public sealed record GalleryItemUpdateRequest(string? Caption,int SortOrder,bool IsVisible=true);
public sealed record GalleryFile(string Path,string ContentType);
