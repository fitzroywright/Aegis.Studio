using System.Security.Claims;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Galleries;
using Common.Storage;
using Aegis.Studio.Web.Notifications;
using Npgsql;

namespace Aegis.Studio.Web.Operations;

public static class JobLifecycleRules
{
    public static string Target(string action)=>action switch
    {
        "submit-review"=>"Needs Review",
        "request-changes"=>"Changes Requested",
        "approve"=>"In Production",
        "finish-production"=>"Ready for Client",
        "publish"=>"Published",
        _=>throw new ArgumentException("Choose a supported job action.")
    };
    public static bool Allowed(string status,string action)=>action switch
    {
        "submit-review"=>status is "Awaiting Upload" or "Changes Requested",
        "request-changes"=>status is "Needs Review" or "In Production" or "Ready for Client",
        "approve"=>status=="Needs Review",
        "finish-production"=>status=="In Production",
        "publish"=>status=="Ready for Client",
        _=>false
    };
    public static void Validate(string status,string action,int mediaCount,bool openProduction,bool publishConfigured)
    {
        if(!Allowed(status,action))throw new ArgumentException("This action is unavailable at the job's current stage. Refresh the job.");
        if(action is "submit-review" or "approve" or "finish-production" or "publish" && mediaCount==0)
            throw new ArgumentException("Upload job media before continuing.");
        if(action=="finish-production"&&openProduction)throw new ArgumentException("Complete or cancel the outstanding production tasks before finishing production.");
        if(action=="publish"&&!publishConfigured)throw new ArgumentException("A client gallery provider must be configured before publishing.");
    }
}

public sealed record JobLifecycleRequest(string Action,string? Note,string ExpectedStatus,Guid OperationId);

public static class JobLifecycleEndpoints
{
    public static void MapJobLifecycle(this WebApplication app)
    {
        using(var connection=app.Services.GetRequiredService<StudioDatabase>().OpenConnection())
        using(var command=new NpgsqlCommand("create table if not exists studio_job_workflow_actions(operation_id uuid primary key,job_id uuid not null references studio_jobs(id),action text not null,result_status text not null,acted_by text not null,acted_at_utc timestamptz not null default now())",connection))command.ExecuteNonQuery();
        app.MapGet("/api/jobs/{jobId:guid}/workflow",(Guid jobId,ClaimsPrincipal user,JobAccessService access,StudioStore store,IConfiguration configuration)=>
        {
            if(!access.HasPermission(user,jobId,"ViewJob"))return Results.Forbid();
            StudioJob? job=store.GetJob(jobId);if(job is null)return Results.NotFound();
            bool editor=access.HasPermission(user,jobId,"ManageProduction")&&access.HasPermission(user,jobId,"ChangeStatus");
            bool reviewer=access.HasPermission(user,jobId,"ChangeStatus");
            return Results.Ok(new{job.Status,actions=new[]{"submit-review","request-changes","approve","finish-production","publish"}.Where(a=>editor&&JobLifecycleRules.Allowed(job.Status,a)&&(!(a is "approve" or "request-changes")||reviewer)),galleryProvider=configuration["ClientGallery:Provider"]??"Unconfigured"});
        }).RequireAuthorization();
        app.MapPost("/api/jobs/{jobId:guid}/workflow",async(Guid jobId,JobLifecycleRequest request,ClaimsPrincipal user,JobAccessService access,StudioDatabase database,IConfiguration configuration,IClientGalleryPublisher publisher,JobEventNotifier notifier,ILoggerFactory logs,CancellationToken token)=>
        {
            if(!access.HasPermission(user,jobId,"ManageProduction")||!access.HasPermission(user,jobId,"ChangeStatus"))return Results.Forbid();
            try
            {
                if((request.Note?.Length??0)>4000)throw new ArgumentException("Notes must be at most 4000 characters.");
                if(request.OperationId==Guid.Empty)throw new ArgumentException("An operation reference is required for safe retries.");
                if(request.Action=="request-changes"&&string.IsNullOrWhiteSpace(request.Note))throw new ArgumentException("Describe the changes needed.");
                string actor=user.FindFirstValue("login_name")??user.Identity?.Name??"Studio";
                using var connection=database.OpenConnection();using var transaction=connection.BeginTransaction();
                Execute(connection,transaction,"select pg_advisory_xact_lock(hashtextextended($1,0))","job-action:"+request.OperationId);
                using(var previous=new NpgsqlCommand("select job_id,action,result_status from studio_job_workflow_actions where operation_id=$1",connection,transaction))
                {
                    previous.Parameters.AddWithValue(request.OperationId);using var reader=previous.ExecuteReader();
                    if(reader.Read())
                    {
                        if(reader.GetGuid(0)!=jobId||reader.GetString(1)!=request.Action)throw new ArgumentException("This operation reference belongs to another action.");
                        return Results.Ok(new{status=reader.GetString(2),alreadyApplied=true});
                    }
                }
                // All lifecycle actions serialize on the existing Job row, including safe repeats.
                using var get=new NpgsqlCommand("select status,title from studio_jobs where id=$1 for update",connection,transaction);get.Parameters.AddWithValue(jobId);
                string status,title;using(var reader=get.ExecuteReader()){if(!reader.Read())return Results.NotFound();status=reader.GetString(0);title=reader.GetString(1);}
                string target=JobLifecycleRules.Target(request.Action);
                if(status==target)return Results.Ok(new{status,alreadyApplied=true});
                if(status!=request.ExpectedStatus)throw new ArgumentException("The job changed since you opened it. Refresh and try again.");
                using var count=new NpgsqlCommand("select count(*) from studio_media_assets where job_id=$1",connection,transaction);count.Parameters.AddWithValue(jobId);int mediaCount=Convert.ToInt32(count.ExecuteScalar());
                using var tasks=new NpgsqlCommand("select exists(select 1 from studio_job_activities where job_id=$1 and status not in ('Completed','Cancelled'))",connection,transaction);tasks.Parameters.AddWithValue(jobId);bool openTasks=(bool)tasks.ExecuteScalar()!;
                string provider=configuration["ClientGallery:Provider"]??"";
                JobLifecycleRules.Validate(status,request.Action,mediaCount,openTasks,provider=="Manifest");
                string? receipt=null;
                if(request.Action=="publish")
                {
                    var media=new List<ClientGalleryMedia>();
                    using(var assets=new NpgsqlCommand("select a.asset_key,v.id,v.file_name,v.stored_path,v.content_type from studio_media_assets a join studio_asset_versions v on v.id=a.approved_version_id where a.job_id=$1 order by a.asset_key",connection,transaction))
                    {
                        assets.Parameters.AddWithValue(jobId);using var reader=assets.ExecuteReader();
                        while(reader.Read())media.Add(new(reader.GetString(0),reader.GetGuid(1),reader.GetString(2),"VersionStore/"+reader.GetString(3),reader.GetString(4)));
                    }
                    if(media.Count!=mediaCount)throw new ArgumentException("Finish quality control for all job media before publication.");
                    var publication=await publisher.PublishAsync(new ClientGalleryPublishRequest(jobId,title,media),token);
                    receipt=publication.Reference;
                }
                Execute(connection,transaction,"update studio_jobs set status=$2 where id=$1",jobId,target);
                if(request.Action is "approve" or "finish-production")
                    Execute(connection,transaction,"update studio_media_assets a set approved_version_id=(select id from studio_asset_versions where job_id=a.job_id and asset_key=a.asset_key order by version_number desc limit 1) where a.job_id=$1",jobId);
                if(request.Action=="approve")
                {
                    Execute(connection,transaction,"update studio_media_assets set production_status='Ready for Production' where job_id=$1",jobId);
                    Execute(connection,transaction,"""
                        insert into studio_job_activities(id,job_id,activity_type,title,status,priority,created_by,created_at_utc,updated_at_utc)
                        select $2,$1,'QC','Complete production and quality control','NotStarted','Normal',$3,now(),now()
                        where not exists(select 1 from studio_job_activities where job_id=$1 and title='Complete production and quality control')
                        """,jobId,Guid.NewGuid(),actor);
                }
                Execute(connection,transaction,"insert into studio_operational_audit(id,job_id,activity_id,action,user_name,detail,created_at_utc) values($1,$2,null,$3,$4,$5,now())",Guid.NewGuid(),jobId,"Workflow:"+request.Action,actor,request.Note??$"{status} → {target}");
                using var conversation=new NpgsqlCommand("select conversation_id from studio_jobs where id=$1",connection,transaction);conversation.Parameters.AddWithValue(jobId);Guid conversationId=(Guid)conversation.ExecuteScalar()!;
                Execute(connection,transaction,"insert into studio_messages(id,conversation_id,author_display_name,visibility,body,created_at_utc) values($1,$2,$3,'Internal',$4,now())",Guid.NewGuid(),conversationId,actor,$"{status} → {target}. {request.Note}");
                Execute(connection,transaction,"insert into studio_job_workflow_actions(operation_id,job_id,action,result_status,acted_by) values($1,$2,$3,$4,$5)",request.OperationId,jobId,request.Action,target,actor);
                transaction.Commit();
                try{notifier.Publish(jobId,"JobStatusChanged",$"Job update: {title}",$"{status} → {target}");}
                catch(Exception error){logs.CreateLogger("JobLifecycle").LogError(error,"Job action completed but notification enqueue failed for {JobId}",jobId);}
                return Results.Ok(new{status=target,publicationReference=receipt});
            }
            catch(ArgumentException e){return Results.BadRequest(new{error=e.Message});}
            catch(StorageException e){logs.CreateLogger("JobLifecycle").LogError(e,"Gallery publication failed for {JobId}",jobId);return Results.Problem("Gallery publication could not be completed. The job remains ready for retry.",statusCode:503);}
        }).RequireAuthorization();
    }
    private static void Execute(NpgsqlConnection connection,NpgsqlTransaction transaction,string sql,params object[] values)
    {using var command=new NpgsqlCommand(sql,connection,transaction);foreach(var value in values)command.Parameters.AddWithValue(value);command.ExecuteNonQuery();}
}
