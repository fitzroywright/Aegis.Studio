using System.Security.Claims;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Versioning;
using Common.Storage;
using Npgsql;

namespace Aegis.Studio.Web.Media;

public static class MediaProductionEndpoints
{
    public static void MapMediaProduction(this WebApplication app)
    {
        MediaProductionStore.Migrate(app.Services.GetRequiredService<StudioDatabase>());
        app.MapGet("/api/my-work/media",(ClaimsPrincipal user,JobAccessService access,StudioDatabase db)=>
        {
            var allowed=access.GetAccessibleJobIds(user).Where(id=>access.HasPermission(user,id,"ViewAttachments")).ToArray();
            using var c=db.OpenConnection();using var cmd=new NpgsqlCommand("select a.id,a.job_id,j.title,a.production_status,coalesce(v.file_name,a.asset_key),not exists(select 1 from studio_media_asset_subjects s where s.asset_id=a.id) and exists(select 1 from studio_job_subjects s where s.job_id=j.id) from studio_media_assets a join studio_jobs j on j.id=a.job_id left join lateral(select file_name from studio_asset_versions where job_id=a.job_id and asset_key=a.asset_key order by version_number desc limit 1)v on true where a.job_id=any($1) and lower(coalesce(a.assigned_to,j.assigned_to,''))=lower($2) and a.production_status not in ('Completed','Ready') order by j.created_at_utc,a.id",c);
            cmd.Parameters.AddWithValue(allowed);cmd.Parameters.AddWithValue(Name(user));using var r=cmd.ExecuteReader();var rows=new List<object>();while(r.Read())rows.Add(new{assetId=r.GetGuid(0),jobId=r.GetGuid(1),jobTitle=r.GetString(2),status=r.GetBoolean(5)?"Needs Subject Matching":r.GetString(3),fileName=r.GetString(4)});r.Close();
            using var subjects=new NpgsqlCommand("select s.id,s.job_id,j.title,s.name,s.production_status from studio_job_subjects s join studio_jobs j on j.id=s.job_id where s.job_id=any($1) and lower(coalesce(j.assigned_to,''))=lower($2) and s.production_status not in ('Ready','Completed')",c);subjects.Parameters.AddWithValue(allowed);subjects.Parameters.AddWithValue(Name(user));using var sr=subjects.ExecuteReader();while(sr.Read())rows.Add(new{subjectId=sr.GetGuid(0),jobId=sr.GetGuid(1),jobTitle=sr.GetString(2),fileName=sr.GetString(3),status=sr.GetString(4)=="Not Photographed"?"Needs Ingest / Sorting":sr.GetString(4)});return Results.Ok(rows);
        }).RequireAuthorization();
        app.MapGet("/api/jobs/{jobId:guid}/media",(Guid jobId,ClaimsPrincipal user,JobAccessService access,StudioDatabase db,IFileStorage storage)=>access.HasPermission(user,jobId,"ViewAttachments")?Results.Ok(Store(db,storage).Snapshot(jobId)):Results.Forbid()).RequireAuthorization();
        app.MapPut("/api/jobs/{jobId:guid}/media/settings",(Guid jobId,MediaGallerySettings request,ClaimsPrincipal user,JobAccessService access,StudioDatabase db,IFileStorage storage)=>Write(jobId,user,access,()=>{
            if(string.IsNullOrWhiteSpace(request.Name)||request.Name.Length>100||!new[]{"Auto","Images","Subjects","Collections"}.Contains(request.DefaultLens))throw new ArgumentException("Choose a media name and default view.");
            Store(db,storage).Sync(jobId);using var c=db.OpenConnection();using var tx=c.BeginTransaction();MediaProductionStore.Execute(c,tx,"update studio_media_galleries set name=$2,default_lens=$3 where id=$1",jobId,request.Name.Trim(),request.DefaultLens);MediaProductionStore.Audit(c,tx,jobId,"MediaSettingsChanged",Name(user),"Updated media name and default view.");tx.Commit();return Results.NoContent();
        })).RequireAuthorization();
        app.MapPut("/api/jobs/{jobId:guid}/media/assets/{assetId:guid}",(Guid jobId,Guid assetId,MediaAssetUpdate request,ClaimsPrincipal user,JobAccessService access,StudioDatabase db,IFileStorage storage)=>Write(jobId,user,access,()=>{Store(db,storage).UpdateAsset(jobId,assetId,request,Name(user));return Results.NoContent();})).RequireAuthorization();
        app.MapPost("/api/jobs/{jobId:guid}/media/collections",(Guid jobId,MediaCollectionRequest request,ClaimsPrincipal user,JobAccessService access,StudioDatabase db,IFileStorage storage)=>Write(jobId,user,access,()=>{Store(db,storage).CreateCollection(jobId,request.Name,Name(user));return Results.NoContent();})).RequireAuthorization();
        app.MapPut("/api/jobs/{jobId:guid}/media/collections/{name}",(Guid jobId,string name,MediaCollectionRequest request,ClaimsPrincipal user,JobAccessService access,StudioDatabase db,IFileStorage storage)=>Write(jobId,user,access,()=>{Store(db,storage).RenameCollection(jobId,name,request.Name,Name(user));return Results.NoContent();})).RequireAuthorization();
        app.MapPut("/api/jobs/{jobId:guid}/media/collections/default",(Guid jobId,MediaCollectionRequest request,ClaimsPrincipal user,JobAccessService access,StudioDatabase db,IFileStorage storage)=>Write(jobId,user,access,()=>{Store(db,storage).SetDefaultCollection(jobId,request.Name,Name(user));return Results.NoContent();})).RequireAuthorization();
        app.MapPost("/api/jobs/{jobId:guid}/media/roster",(Guid jobId,RosterImport request,ClaimsPrincipal user,JobAccessService access,StudioDatabase db,IFileStorage storage)=>Write(jobId,user,access,()=>Results.Ok(new{count=Store(db,storage).ImportSubjects(jobId,request,Name(user))}))).RequireAuthorization();
        app.MapPut("/api/jobs/{jobId:guid}/media/subjects/{subjectId:guid}/status",(Guid jobId,Guid subjectId,SubjectStateRequest request,ClaimsPrincipal user,JobAccessService access,StudioDatabase db,IFileStorage storage)=>Write(jobId,user,access,()=>{Store(db,storage).SubjectStatus(jobId,subjectId,request.Status,Name(user));return Results.NoContent();})).RequireAuthorization();
        app.MapPost("/api/jobs/{jobId:guid}/media/export",(Guid jobId,MediaExportRequest request,ClaimsPrincipal user,JobAccessService access,StudioDatabase db,IFileStorage storage)=>Write(jobId,user,access,()=>Results.File(Store(db,storage).Export(jobId,request.Assets??[],Name(user)),"application/zip","studio-editing-batch.zip"))).RequireAuthorization();
        app.MapPost("/api/jobs/{jobId:guid}/media/uploads",async(Guid jobId,HttpRequest request,ClaimsPrincipal user,JobAccessService access,StudioDatabase db,IFileStorage storage)=>
        {
            if(!access.HasPermission(user,jobId,"ManageProduction"))return Results.Forbid();
            if(!request.HasFormContentType)return Results.BadRequest(new{error="Select files to upload."});
            var form=await request.ReadFormAsync();if(form.Files.Count!=1)return Results.BadRequest(new{error="Send one file per request; the client queues batch uploads."});
            Guid? asset=Guid.TryParse(form["assetId"],out var parsed)?parsed:null;
            return Write(jobId,user,access,()=>Results.Ok(Store(db,storage).Ingest(jobId,form.Files[0],form["returned"]=="true",asset,Name(user),form["note"].ToString(),form["collection"].ToString())));
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(101_000_000)).DisableAntiforgery().RequireAuthorization();
        app.MapPost("/api/jobs/{jobId:guid}/media/unmatched/{id:guid}/match",(Guid jobId,Guid id,MatchUploadRequest request,ClaimsPrincipal user,JobAccessService access,StudioDatabase db,IFileStorage storage)=>Write(jobId,user,access,()=>{Store(db,storage).Resolve(jobId,id,request.AssetId,Name(user));return Results.NoContent();})).RequireAuthorization();
        app.MapGet("/api/workspaces/{workspaceId:guid}/appearance",(Guid workspaceId,ClaimsPrincipal user,WorkspaceAccessService access,StudioDatabase db)=>
        {
            if(!access.HasPermission(user,workspaceId,"AccessWorkspace"))return Results.Forbid();
            using var c=db.OpenConnection();using var cmd=new NpgsqlCommand("select icon_name,accent_color from studio_workspaces where id=$1",c);cmd.Parameters.AddWithValue(workspaceId);using var r=cmd.ExecuteReader();return r.Read()?Results.Ok(new{icon=r.GetString(0),color=r.GetString(1)}):Results.NotFound();
        }).RequireAuthorization();
        app.MapPut("/api/workspaces/{workspaceId:guid}/appearance",(Guid workspaceId,WorkspaceAppearance request,ClaimsPrincipal user,WorkspaceAccessService access,StudioDatabase db)=>
        {
            if(!access.HasPermission(user,workspaceId,"EditWorkspace"))return Results.Forbid();
            if(!new[]{"folder","camera","portrait","wedding","id"}.Contains(request.Icon)||!System.Text.RegularExpressions.Regex.IsMatch(request.Color??"","^#[0-9a-fA-F]{6}$"))return Results.BadRequest(new{error="Choose an icon and a valid color."});
            using var c=db.OpenConnection();using var cmd=new NpgsqlCommand("update studio_workspaces set icon_name=$2,accent_color=$3 where id=$1",c);cmd.Parameters.AddWithValue(workspaceId);cmd.Parameters.AddWithValue(request.Icon);cmd.Parameters.AddWithValue(request.Color!);cmd.ExecuteNonQuery();return Results.NoContent();
        }).RequireAuthorization();
    }
    private static MediaProductionStore Store(StudioDatabase db,IFileStorage storage)=>new(db,new StudioVersionStore(db,storage));
    private static string Name(ClaimsPrincipal user)=>user.FindFirstValue("login_name")??user.Identity?.Name??throw new UnauthorizedAccessException();
    private static IResult Write(Guid job,ClaimsPrincipal user,JobAccessService access,Func<IResult> action)
    {
        if(!access.HasPermission(user,job,"ManageProduction"))return Results.Forbid();
        try{return action();}catch(ArgumentException e){return Results.BadRequest(new{error=e.Message});}catch(KeyNotFoundException){return Results.NotFound();}
    }
}
public sealed record WorkspaceAppearance(string Icon,string Color);

public sealed record MediaGallerySettings(string Name,string DefaultLens);
public sealed record MediaCollectionRequest(string Name);
