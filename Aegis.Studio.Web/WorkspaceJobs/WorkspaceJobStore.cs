using System.Security.Claims;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.WorkspaceJobs;

// Additive schema: legacy conversations, attachments and financial records remain intact.
public sealed class WorkspaceJobStore(StudioDatabase database)
{
    public void EnsureSchema()
    {
        string[] statements = [
            "create table if not exists studio_workspace_announcements(id uuid primary key, workspace_id uuid not null references studio_workspaces(id) on delete cascade, title text not null, body text not null, created_by text not null, created_at_utc timestamptz not null default now())",
            "create index if not exists ix_workspace_announcements on studio_workspace_announcements(workspace_id, created_at_utc desc)",
            "create table if not exists studio_job_subjects(id uuid primary key, job_id uuid not null references studio_jobs(id) on delete cascade, name text not null, reference text not null default '', notes text not null default '', updated_by text not null, updated_at_utc timestamptz not null default now())",
            "create index if not exists ix_job_subjects on studio_job_subjects(job_id)",
            "create table if not exists studio_job_asset_collections(job_id uuid not null references studio_jobs(id) on delete cascade, asset_key text not null, collection_name text not null, primary key(job_id,asset_key))"
        ];
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (string sql in statements)
        {
            using var command = new NpgsqlCommand(sql, connection, transaction);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IReadOnlyList<WorkspaceAnnouncement> Announcements(Guid workspaceId)
    {
        using var connection = database.OpenConnection();
        using var command = new NpgsqlCommand("select id,title,body,created_by,created_at_utc from studio_workspace_announcements where workspace_id=$1 order by created_at_utc desc", connection);
        command.Parameters.AddWithValue(workspaceId);
        using var reader = command.ExecuteReader();
        List<WorkspaceAnnouncement> result = [];
        while(reader.Read()) result.Add(new(reader.GetGuid(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetFieldValue<DateTimeOffset>(4)));
        return result;
    }

    public Guid AddAnnouncement(Guid workspaceId, AnnouncementRequest request, string user)
    {
        RequireText(request.Title, 200, "Title"); RequireText(request.Body, 10000, "Announcement");
        using var connection = database.OpenConnection();
        using var command = new NpgsqlCommand("insert into studio_workspace_announcements(id,workspace_id,title,body,created_by) values($1,$2,$3,$4,$5)", connection);
        Guid id = Guid.NewGuid();
        foreach(object value in new object[]{id, workspaceId, request.Title.Trim(), request.Body.Trim(), user}) command.Parameters.AddWithValue(value);
        command.ExecuteNonQuery(); return id;
    }

    public IReadOnlyList<JobSubject> Subjects(Guid jobId)
    {
        using var connection = database.OpenConnection();
        using var command = new NpgsqlCommand("select id,name,reference,notes from studio_job_subjects where job_id=$1 order by name,id", connection);
        command.Parameters.AddWithValue(jobId);
        using var reader = command.ExecuteReader();
        List<JobSubject> result = [];
        while(reader.Read()) result.Add(new(reader.GetGuid(0),reader.GetString(1),reader.GetString(2),reader.GetString(3)));
        return result;
    }

    public bool SaveSubject(Guid jobId, Guid id, SubjectRequest request, string user, bool create)
    {
        RequireText(request.Name, 200, "Name");
        if ((request.Reference?.Length ?? 0)>200 || (request.Notes?.Length ?? 0)>4000) throw new ArgumentException("Subject reference or notes are too long.");
        using var connection = database.OpenConnection();
        string sql = create
            ? "insert into studio_job_subjects(id,job_id,name,reference,notes,updated_by) values($1,$2,$3,$4,$5,$6)"
            : "update studio_job_subjects set name=$3,reference=$4,notes=$5,updated_by=$6,updated_at_utc=now() where id=$1 and job_id=$2";
        using var command = new NpgsqlCommand(sql, connection);
        foreach(object value in new object[]{id,jobId,request.Name.Trim(),request.Reference?.Trim() ?? "",request.Notes?.Trim() ?? "",user}) command.Parameters.AddWithValue(value);
        return command.ExecuteNonQuery()==1;
    }

    public IReadOnlyList<JobGalleryAsset> Assets(Guid jobId)
    {
        using var connection = database.OpenConnection();
        using var command = new NpgsqlCommand("select distinct on(v.asset_key) v.asset_key,v.id,v.version_number,v.file_name,v.created_by,v.created_at_utc,v.sha256,v.source_version_id,coalesce(c.collection_name,'Uncollected') from studio_asset_versions v left join studio_job_asset_collections c on c.job_id=v.job_id and c.asset_key=v.asset_key where v.job_id=$1 order by v.asset_key,v.version_number desc", connection);
        command.Parameters.AddWithValue(jobId);
        using var reader = command.ExecuteReader();
        List<JobGalleryAsset> result = [];
        while(reader.Read()) result.Add(new(reader.GetString(0),reader.GetGuid(1),reader.GetInt32(2),reader.GetString(3),reader.GetString(4),reader.GetFieldValue<DateTimeOffset>(5),reader.GetString(6),reader.IsDBNull(7)?null:reader.GetGuid(7),reader.GetString(8)));
        return result;
    }

    public bool SetCollection(Guid jobId, string assetKey, string collection)
    {
        RequireText(collection,100,"Collection");
        using var connection = database.OpenConnection();
        using var command = new NpgsqlCommand("insert into studio_job_asset_collections(job_id,asset_key,collection_name) select $1,$2,$3 where exists(select 1 from studio_asset_versions where job_id=$1 and asset_key=$2) on conflict(job_id,asset_key) do update set collection_name=excluded.collection_name", connection);
        command.Parameters.AddWithValue(jobId);command.Parameters.AddWithValue(assetKey);command.Parameters.AddWithValue(collection.Trim());
        return command.ExecuteNonQuery()==1;
    }

    private static void RequireText(string? value, int max, string label)
    {
        if(string.IsNullOrWhiteSpace(value) || value.Length>max) throw new ArgumentException($"{label} is required and must be at most {max} characters.");
    }
}

public sealed record WorkspaceAnnouncement(Guid Id,string Title,string Body,string CreatedBy,DateTimeOffset CreatedAtUtc);
public sealed record AnnouncementRequest(string Title,string Body);
public sealed record JobSubject(Guid Id,string Name,string Reference,string Notes);
public sealed record SubjectRequest(string Name,string? Reference,string? Notes);
public sealed record CollectionRequest(string Name);
public sealed record JobGalleryAsset(string AssetKey,Guid Id,int VersionNumber,string FileName,string CreatedBy,DateTimeOffset CreatedAtUtc,string Sha256,Guid? SourceVersionId,string Collection);

public static class WorkspaceJobEndpoints
{
    public static void MapWorkspaceJobs(this WebApplication app)
    {
        app.MapGet("/api/workspaces/{workspaceId:guid}/announcements", (Guid workspaceId,ClaimsPrincipal user,WorkspaceAccessService access,StudioDatabase db) =>
            access.HasPermission(user,workspaceId,"AccessWorkspace") && access.HasPermission(user,workspaceId,"View") ? Results.Ok(new WorkspaceJobStore(db).Announcements(workspaceId)) : Results.Forbid()).RequireAuthorization();
        app.MapPost("/api/workspaces/{workspaceId:guid}/announcements", (Guid workspaceId,AnnouncementRequest request,ClaimsPrincipal user,WorkspaceAccessService access,StudioDatabase db) =>
        {
            if(!access.HasPermission(user,workspaceId,"AccessWorkspace") || !access.HasPermission(user,workspaceId,"NotifyMembers")) return Results.Forbid();
            return Validated(()=>Results.Ok(new {id=new WorkspaceJobStore(db).AddAnnouncement(workspaceId,request,UserName(user))}));
        }).RequireAuthorization();
        app.MapGet("/api/jobs/{jobId:guid}/subjects", (Guid jobId,ClaimsPrincipal user,JobAccessService access,StudioDatabase db) => access.HasPermission(user,jobId,"ViewJob") ? Results.Ok(new WorkspaceJobStore(db).Subjects(jobId)) : Results.Forbid()).RequireAuthorization();
        app.MapPost("/api/jobs/{jobId:guid}/subjects", (Guid jobId,SubjectRequest request,ClaimsPrincipal user,JobAccessService access,StudioDatabase db) =>
        {
            if(!access.HasPermission(user,jobId,"EditJob")) return Results.Forbid();
            return Validated(()=>{Guid id=Guid.NewGuid(); new WorkspaceJobStore(db).SaveSubject(jobId,id,request,UserName(user),true); return Results.Ok(new{id});});
        }).RequireAuthorization();
        app.MapPut("/api/jobs/{jobId:guid}/subjects/{id:guid}", (Guid jobId,Guid id,SubjectRequest request,ClaimsPrincipal user,JobAccessService access,StudioDatabase db) =>
        {
            if(!access.HasPermission(user,jobId,"EditJob")) return Results.Forbid();
            return Validated(()=>new WorkspaceJobStore(db).SaveSubject(jobId,id,request,UserName(user),false) ? Results.NoContent() : Results.NotFound());
        }).RequireAuthorization();
        app.MapGet("/api/jobs/{jobId:guid}/gallery-assets", (Guid jobId,ClaimsPrincipal user,JobAccessService access,StudioDatabase db) => access.HasPermission(user,jobId,"ViewAttachments") ? Results.Ok(new WorkspaceJobStore(db).Assets(jobId)) : Results.Forbid()).RequireAuthorization();
        app.MapPut("/api/jobs/{jobId:guid}/gallery-assets/{assetKey}/collection", (Guid jobId,string assetKey,CollectionRequest request,ClaimsPrincipal user,JobAccessService access,StudioDatabase db) =>
        {
            if(!access.HasPermission(user,jobId,"ManageProduction")) return Results.Forbid();
            return Validated(()=>new WorkspaceJobStore(db).SetCollection(jobId,assetKey,request.Name) ? Results.NoContent() : Results.NotFound());
        }).RequireAuthorization();
    }
    private static string UserName(ClaimsPrincipal user)=>user.FindFirstValue("login_name") ?? user.Identity?.Name ?? "Studio";
    private static IResult Validated(Func<IResult> action)
    {
        try {return action();} catch(ArgumentException ex){return Results.BadRequest(new{error=ex.Message});}
    }
}
