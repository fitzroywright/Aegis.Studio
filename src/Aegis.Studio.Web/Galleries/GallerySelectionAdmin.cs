using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Galleries;

public static class GallerySelectionAdminEndpoints
{
    public static void MapStudioGallerySelectionAdmin(this WebApplication app)
    {
        StudioDatabase database = app.Services.GetRequiredService<StudioDatabase>();

        app.MapGet("/api/workspaces/{workspaceId:guid}/gallery/selection-review", (Guid workspaceId, HttpContext context, WorkspaceAccessService access) =>
        {
            if (!context.User.IsInRole("Administrator") && !access.HasPermission(context.User, workspaceId, "ManageProduction") && !access.HasPermission(context.User, workspaceId, "AdminWorkspace")) return Results.Forbid();
            using NpgsqlConnection connection = database.OpenConnection();
            Guid? galleryId = GetGalleryId(connection, workspaceId);
            if (!galleryId.HasValue) return Results.NotFound();
            IReadOnlyList<GallerySelectedItemReview> selections = GetSelections(connection, galleryId.Value);
            IReadOnlyList<GallerySelectionSubmissionReview> submissions = GetSubmissions(connection, galleryId.Value);
            return Results.Ok(new { selections, submissions });
        }).RequireAuthorization();
    }

    private static Guid? GetGalleryId(NpgsqlConnection connection, Guid workspaceId)
    {
        using NpgsqlCommand command=connection.CreateCommand();command.CommandText="select id from studio_workspace_galleries where workspace_id=$1";command.Parameters.AddWithValue(workspaceId);object? value=command.ExecuteScalar();return value is Guid id?id:null;
    }

    private static IReadOnlyList<GallerySelectedItemReview> GetSelections(NpgsqlConnection connection, Guid galleryId)
    {
        using NpgsqlCommand command=connection.CreateCommand();command.CommandText="""
            select i.id,i.file_name,i.caption,s.note,s.updated_at_utc
            from studio_gallery_selections s
            join studio_gallery_items i on i.id=s.item_id
            where s.gallery_id=$1 and s.selected
            order by i.sort_order,i.created_at_utc
            """;command.Parameters.AddWithValue(galleryId);using NpgsqlDataReader reader=command.ExecuteReader();List<GallerySelectedItemReview> values=[];while(reader.Read())values.Add(new(reader.GetGuid(0),reader.GetString(1),reader.IsDBNull(2)?null:reader.GetString(2),reader.IsDBNull(3)?null:reader.GetString(3),reader.GetFieldValue<DateTimeOffset>(4)));return values;
    }

    private static IReadOnlyList<GallerySelectionSubmissionReview> GetSubmissions(NpgsqlConnection connection, Guid galleryId)
    {
        using NpgsqlCommand command=connection.CreateCommand();command.CommandText="select id,client_name,client_email,note,selection_count,submitted_at_utc from studio_gallery_selection_submissions where gallery_id=$1 order by submitted_at_utc desc";command.Parameters.AddWithValue(galleryId);using NpgsqlDataReader reader=command.ExecuteReader();List<GallerySelectionSubmissionReview> values=[];while(reader.Read())values.Add(new(reader.GetGuid(0),reader.IsDBNull(1)?null:reader.GetString(1),reader.IsDBNull(2)?null:reader.GetString(2),reader.IsDBNull(3)?null:reader.GetString(3),reader.GetInt32(4),reader.GetFieldValue<DateTimeOffset>(5)));return values;
    }
}

public sealed record GallerySelectedItemReview(Guid ItemId,string FileName,string? Caption,string? Note,DateTimeOffset SelectedAtUtc);
public sealed record GallerySelectionSubmissionReview(Guid Id,string? ClientName,string? ClientEmail,string? Note,int SelectionCount,DateTimeOffset SubmittedAtUtc);
