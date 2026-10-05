using System.Security.Claims;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Npgsql;
namespace Aegis.Studio.Web.Media;

public static class MediaBulkSelection
{
    public static void MapMediaBulkSelection(this WebApplication app)
    {
        app.MapPut("/api/jobs/{jobId:guid}/media/classification",(Guid jobId,MediaClassificationRequest request,ClaimsPrincipal user,JobAccessService access,StudioDatabase db)=>
        {
            if(!access.HasPermission(user,jobId,"ManageProduction"))return Results.Forbid();
            try
            {
                return Results.Ok(new{affected=Apply(db,jobId,request,user.FindFirstValue("login_name")??user.Identity?.Name??"Studio")});
            }
            catch(ArgumentException error){return Results.BadRequest(new{error=error.Message});}
        }).RequireAuthorization();
    }
    public static int Apply(StudioDatabase db,Guid jobId,MediaClassificationRequest request,string actor)
    {
                request.Filter?.Validate();
                if(!request.AllMatching&&(request.Assets is null||request.Assets.Length==0||request.Assets.Length>2000))throw new ArgumentException("Select images or all matching images.");
                if(request.AllMatching&&request.Filter is null)throw new ArgumentException("All matching selection requires its filter.");
                using var c=db.OpenConnection();using var tx=c.BeginTransaction();
                using(var guard=new NpgsqlCommand("select id from studio_jobs where id=$1 and status not in ('Published','Completed','Cancelled') for update",c,tx))
                {guard.Parameters.AddWithValue(jobId);if(guard.ExecuteScalar() is null)throw new KeyNotFoundException("Job not found.");}
                Guid target;
                using(var subject=new NpgsqlCommand("select id from studio_job_subjects where job_id=$1 and ((id=$2) or ($2::uuid is null and is_system and lower(name)='unclassified')) limit 1",c,tx))
                {subject.Parameters.AddWithValue(jobId);subject.Parameters.AddWithValue((object?)request.SubjectId??DBNull.Value);target=subject.ExecuteScalar() is Guid id?id:throw new ArgumentException("Choose a subject belonging to this job.");}
                // Materialize the selection in SQL, independent of loaded browser pages.
                MediaProductionStore.Execute(c,tx,"create temporary table studio_selected_media(asset_id uuid primary key) on commit drop");
                using(var select=new NpgsqlCommand("""
                    insert into studio_selected_media
                    select a.id from studio_media_assets a left join studio_job_asset_collections col on col.job_id=a.job_id and col.asset_key=a.asset_key
                    where a.job_id=$1 and (($2 and ($3::text is null or coalesce(col.collection_name,'Unclassified')=$3)
                      and ($4::uuid is null or exists(select 1 from studio_media_asset_subjects s where s.asset_id=a.id and s.subject_id=$4))
                      and ($5::text is null or a.production_status=$5) and ($7::uuid is null or a.id=$7)) or (not $2 and a.id=any($6)))
                    """,c,tx))
                {
                    foreach(var v in new object?[]{jobId,request.AllMatching,request.Filter?.Collection,request.Filter?.SubjectId,request.Filter?.Status,request.Assets??[],request.Filter?.AssetId})select.Parameters.AddWithValue(v??DBNull.Value);
                    select.ExecuteNonQuery();
                }
                int affected=Convert.ToInt32(MediaProductionStore.Scalar(c,tx,"select count(*) from studio_selected_media"));
                MediaProductionStore.Execute(c,tx,"delete from studio_media_asset_subjects where asset_id in(select asset_id from studio_selected_media)");
                MediaProductionStore.Execute(c,tx,"insert into studio_media_asset_subjects(asset_id,subject_id) select asset_id,$1 from studio_selected_media",target);
                MediaProductionStore.Audit(c,tx,jobId,"MediaBulkClassified",actor,$"Assigned {affected} matching images to subject {target}; collections retained.");
                tx.Commit();return affected;
    }

}
public sealed record MediaClassificationRequest(bool AllMatching,Guid[]? Assets,MediaPageQuery? Filter,Guid? SubjectId);
