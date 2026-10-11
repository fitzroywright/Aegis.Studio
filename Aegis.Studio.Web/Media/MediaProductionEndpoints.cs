using System.Security.Claims;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Versioning;
using Aegis.Studio.Web.Workflow;
using Common.Storage;
using Npgsql;

namespace Aegis.Studio.Web.Media;

public static class MediaProductionEndpoints
{
    private static readonly MediaIngestQueue ingestQueue=new();
    public static void MapMediaProduction(this WebApplication app)
    {
        MediaProductionStore.Migrate(app.Services.GetRequiredService<StudioDatabase>());
        app.MapGet("/api/today/media",(Guid? jobId,ClaimsPrincipal user,JobAccessService access,StudioDatabase db)=>
        {
            var allowed=access.GetAccessibleJobIds(user,requireAttachments:true).Where(id=>!jobId.HasValue||id==jobId.Value).ToArray();
            using var c=db.OpenConnection();using var cmd=new NpgsqlCommand("select a.id,a.job_id,j.title,a.production_status,coalesce(v.file_name,a.asset_key),not exists(select 1 from studio_media_asset_subjects s where s.asset_id=a.id) and exists(select 1 from studio_job_subjects s where s.job_id=j.id) from studio_media_assets a join studio_jobs j on j.id=a.job_id left join lateral(select file_name from studio_asset_versions where job_id=a.job_id and asset_key=a.asset_key order by version_number desc limit 1)v on true where a.job_id=any($1) and lower(coalesce(a.assigned_to,''))=lower($2) and a.production_status not in ('Completed','Ready') order by j.created_at_utc,a.id limit 200",c);
            cmd.Parameters.AddWithValue(allowed);cmd.Parameters.AddWithValue(Name(user));using var r=cmd.ExecuteReader();var rows=new List<object>();while(r.Read())rows.Add(new{assetId=r.GetGuid(0),jobId=r.GetGuid(1),jobTitle=r.GetString(2),status=r.GetBoolean(5)?"Needs Subject Matching":r.GetString(3),fileName=r.GetString(4)});r.Close();
            return Results.Ok(rows);
        }).RequireAuthorization();
        app.MapGet("/api/production/media-summary",(ClaimsPrincipal user,JobAccessService access,StudioDatabase db)=>
        {
            var allowed=access.GetAccessibleJobIds(user,requireAttachments:true).ToArray();
            using var c=db.OpenConnection();using var cmd=new NpgsqlCommand("""
                select a.job_id,coalesce(col.collection_name,'Unclassified'),count(*),
                  count(*) filter(where a.approved_version_id is not null),
                  count(*) filter(where a.production_status='Completed'),
                  count(*) filter(where a.production_status in ('Blocked','Needs Retake')),
                  array(select v.id from studio_media_assets aa join lateral(select id from studio_asset_versions where job_id=aa.job_id and asset_key=aa.asset_key and content_type like 'image/%' order by version_number desc limit 1) v on true where aa.job_id=a.job_id order by aa.id limit 3)
                from studio_media_assets a left join studio_job_asset_collections col on col.job_id=a.job_id and col.asset_key=a.asset_key
                where a.job_id=any($1) group by a.job_id,coalesce(col.collection_name,'Unclassified')
                """,c);cmd.Parameters.AddWithValue(allowed);using var r=cmd.ExecuteReader();var rows=new List<object>();
            while(r.Read())rows.Add(new{jobId=r.GetGuid(0),collection=r.GetString(1),assetCount=r.GetInt64(2),approvedCount=r.GetInt64(3),completedCount=r.GetInt64(4),blockedCount=r.GetInt64(5),thumbnailVersionIds=r.GetFieldValue<Guid[]>(6)});
            return Results.Ok(rows);
        }).RequireAuthorization();
        app.MapGet("/api/jobs/{jobId:guid}/media",(Guid jobId,string? collection,Guid? subjectId,string? status,Guid? after,int? limit,string? subjectSearch,Guid? assetId,ClaimsPrincipal user,JobAccessService access,StudioDatabase db,IFileStorage storage)=>
        {
            if(!access.HasPermission(user,jobId,"ViewAttachments"))return Results.Forbid();
            try{return Results.Ok(Store(db,storage).Snapshot(jobId,new(collection,subjectId,status,after,limit??100,subjectSearch,assetId)));}
            catch(ArgumentException error){return Results.BadRequest(new{error=error.Message});}
        }).RequireAuthorization();
        app.MapPut("/api/jobs/{jobId:guid}/media/settings",(Guid jobId,MediaGallerySettings request,ClaimsPrincipal user,JobAccessService access,StudioDatabase db,IFileStorage storage)=>Write(jobId,user,access,()=>{
            if(string.IsNullOrWhiteSpace(request.Name)||request.Name.Length>100||!new[]{"Auto","Images","Subjects","Collections"}.Contains(request.DefaultLens))throw new ArgumentException("Choose a media name and default view.");
            Store(db,storage).Sync(jobId);using var c=db.OpenConnection();using var tx=c.BeginTransaction();MediaProductionStore.Execute(c,tx,"update studio_media_galleries set name=$2,default_lens=$3 where id=$1",jobId,request.Name.Trim(),request.DefaultLens);MediaProductionStore.Audit(c,tx,jobId,"MediaSettingsChanged",Name(user),"Updated media name and default view.");tx.Commit();return Results.NoContent();
        })).RequireAuthorization();
        app.MapPut("/api/jobs/{jobId:guid}/media/assets/{assetId:guid}",(Guid jobId,Guid assetId,MediaAssetUpdate request,ClaimsPrincipal user,JobAccessService access,StudioDatabase db,IFileStorage storage)=>Write(jobId,user,access,()=>{Store(db,storage).UpdateAsset(jobId,assetId,request,Name(user),new GlobalOperationalRoleStore(db).HasRole(user,StudioRoleNames.ProductionManager));return Results.NoContent();})).RequireAuthorization();
        app.MapPost("/api/jobs/{jobId:guid}/media/collections",(Guid jobId,MediaCollectionRequest request,ClaimsPrincipal user,JobAccessService access,StudioDatabase db,IFileStorage storage)=>Write(jobId,user,access,()=>{Store(db,storage).CreateCollection(jobId,request.Name,Name(user));return Results.NoContent();})).RequireAuthorization();
        app.MapPut("/api/jobs/{jobId:guid}/media/collections/{name}",(Guid jobId,string name,MediaCollectionRequest request,ClaimsPrincipal user,JobAccessService access,StudioDatabase db,IFileStorage storage)=>Write(jobId,user,access,()=>{Store(db,storage).RenameCollection(jobId,name,request.Name,Name(user));return Results.NoContent();})).RequireAuthorization();
        app.MapPut("/api/jobs/{jobId:guid}/media/collections/default",(Guid jobId,MediaCollectionRequest request,ClaimsPrincipal user,JobAccessService access,StudioDatabase db,IFileStorage storage)=>Write(jobId,user,access,()=>{Store(db,storage).SetDefaultCollection(jobId,request.Name,Name(user));return Results.NoContent();})).RequireAuthorization();
        app.MapPost("/api/jobs/{jobId:guid}/media/roster",(Guid jobId,RosterImport request,ClaimsPrincipal user,JobAccessService access,StudioDatabase db,IFileStorage storage)=>Write(jobId,user,access,()=>Results.Ok(new{count=Store(db,storage).ImportSubjects(jobId,request,Name(user))}))).RequireAuthorization();
        app.MapPost("/api/jobs/{jobId:guid}/media/roster-file",async(Guid jobId,HttpRequest request,ClaimsPrincipal user,JobAccessService access,StudioDatabase db,IFileStorage storage)=>
        {
            if(!access.HasPermission(user,jobId,"ManageProduction"))return Results.Forbid();
            if(!request.HasFormContentType)return Results.BadRequest(new{error="Select a CSV or XLSX roster."});
            var form=await request.ReadFormAsync(request.HttpContext.RequestAborted);
            if(form.Files.Count!=1 || form.Files[0].Length>5_000_000)return Results.BadRequest(new{error="Select one roster file of at most 5 MB."});
            return Write(jobId,user,access,()=>
            {
                using var input=form.Files[0].OpenReadStream();
                string csv=RosterFileReader.Read(input,form.Files[0].FileName);
                return Results.Ok(new{count=Store(db,storage).ImportSubjects(jobId,new RosterImport(csv,form["nameColumn"].ToString(),form["idColumn"].ToString()),Name(user))});
            });
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(6_000_000)).DisableAntiforgery().RequireAuthorization();
        app.MapPut("/api/jobs/{jobId:guid}/media/subjects/{subjectId:guid}/status",(Guid jobId,Guid subjectId,SubjectStateRequest request,ClaimsPrincipal user,JobAccessService access,StudioDatabase db,IFileStorage storage)=>Write(jobId,user,access,()=>{Store(db,storage).SubjectStatus(jobId,subjectId,request.Status,Name(user));return Results.NoContent();})).RequireAuthorization();
        app.MapPost("/api/jobs/{jobId:guid}/media/export",(Guid jobId,MediaExportRequest request,ClaimsPrincipal user,JobAccessService access,StudioDatabase db,IFileStorage storage)=>Write(jobId,user,access,()=>Results.File(Store(db,storage).ExportFile(jobId,request.Assets??[],Name(user)),"application/zip","studio-editing-batch.zip"))).RequireAuthorization();
        app.MapGet("/api/jobs/{jobId:guid}/media/upload-batches",(Guid jobId,ClaimsPrincipal user,JobAccessService access,StudioDatabase db,IFileStorage storage)=>
        {
            if(!access.HasPermission(user,jobId,"ViewAttachments"))return Results.Forbid();
            return Results.Ok(new MediaUploadReceipts(db,Store(db,storage)).List(jobId));
        }).RequireAuthorization();
        app.MapPost("/api/jobs/{jobId:guid}/media/upload-batches",(Guid jobId,UploadBatchDeclaration declaration,ClaimsPrincipal user,JobAccessService access,StudioDatabase db,IFileStorage storage,WorkflowStore workflow)=>Write(jobId,user,access,()=>Results.Ok(new MediaUploadReceipts(db,Store(db,storage)).Create(jobId,declaration,Name(user),workflow.CanManage(user,jobId)||new GlobalOperationalRoleStore(db).HasRole(user,StudioRoleNames.ProductionManager))))).RequireAuthorization();
        app.MapPost("/api/jobs/{jobId:guid}/media/upload-batches/{batchId:guid}/cancel",(Guid jobId,Guid batchId,CancelUploadBatch request,ClaimsPrincipal user,JobAccessService access,StudioDatabase db,IFileStorage storage,WorkflowStore workflow)=>Write(jobId,user,access,()=>Results.Ok(new MediaUploadReceipts(db,Store(db,storage)).Cancel(jobId,batchId,Name(user),workflow.CanManage(user,jobId)||workflow.IsProductionManager(user),request.Reason)))).RequireAuthorization();
        app.MapPost("/api/jobs/{jobId:guid}/media/uploads",async(Guid jobId,HttpRequest request,ClaimsPrincipal user,JobAccessService access,StudioDatabase db,IFileStorage storage)=>
        {
            if(!access.HasPermission(user,jobId,"ManageProduction"))return Results.Forbid();
            if(!request.HasFormContentType)return Results.BadRequest(new{error="Select files to upload."});
            using var ingestion=await ingestQueue.AcquireAsync(TimeSpan.FromSeconds(30),request.HttpContext.RequestAborted);
            if(ingestion is null)return Results.Json(new{error="The upload queue is busy. Retry this photo."},statusCode:429);
            var form=await request.ReadFormAsync(request.HttpContext.RequestAborted);if(form.Files.Count!=1)return Results.BadRequest(new{error="Send one file per request; the client queues batch uploads."});
            Guid? asset=Guid.TryParse(form["assetId"],out var parsed)?parsed:null;
            if(!Guid.TryParse(form["uploadId"],out var uploadId))return Results.BadRequest(new{error="Declare an upload batch and send its stable upload ID."});
            return Write(jobId,user,access,()=>Results.Ok(new MediaUploadReceipts(db,Store(db,storage)).Receive(jobId,uploadId,form.Files[0],Name(user))));
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
        try{return action();}catch(ArgumentException e){return Results.BadRequest(new{error=e.Message});}catch(KeyNotFoundException){return Results.NotFound();}catch(UnauthorizedAccessException){return Results.Forbid();}catch(InvalidOperationException e){return Results.Conflict(new{error=e.Message});}
    }
}
public sealed record WorkspaceAppearance(string Icon,string Color);

public sealed record MediaGallerySettings(string Name,string DefaultLens);
public sealed record MediaCollectionRequest(string Name);

public sealed record CancelUploadBatch(string Reason);
