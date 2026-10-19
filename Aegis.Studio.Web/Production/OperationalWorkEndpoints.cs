using Aegis.Studio.Web.Workflow;
using Npgsql;
using System.Security.Claims;
using Aegis.Studio.Web.Notifications;
using Aegis.Studio.Web.Messaging;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;

namespace Aegis.Studio.Web.Production;

public static class OperationalWorkEndpoints
{
    public static void MapStudioOperationalWork(this WebApplication app)
    {
        app.MapGet("/api/operations/summary", (ClaimsPrincipal user, StudioDatabase database, JobAccessService access) =>
        {
            OperationalWorkStore store = new(database);
            HashSet<Guid> allowed = access.GetAccessibleJobIds(user).ToHashSet();
            IReadOnlyList<JobActivity> activities = store.GetActivities().Where(x => allowed.Contains(x.JobId)).ToList();
            IReadOnlyList<ShootSession> shoots = store.GetShoots().Where(x => allowed.Contains(x.JobId)).ToList();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return Results.Ok(new OperationalSummary(shoots.Count(x => x.CallTimeUtc.Date == now.Date && x.Status != "Cancelled"),shoots.Count(x => x.Status == "InProgress"),shoots.Count(x => x.Status == "ShootComplete" && !x.BackupConfirmed),shoots.Count(x => x.ReshootRequired),activities.Count(x => x.Status != "Completed"),activities.Count(x => x.Status == "Blocked"),activities.Count(x => x.Status != "Completed" && x.DueAtUtc.HasValue && x.DueAtUtc.Value < now)));
        }).RequireAuthorization();

        app.MapGet("/api/operations/activities", (Guid? jobId,string? status,string? assignedTo,bool? includeWorkflowDetails,ClaimsPrincipal user,StudioDatabase database,JobAccessService access) => Results.Ok(new OperationalWorkStore(database).GetActivities(jobId,status,assignedTo,includeWorkflowDetails!=false).Where(x=>access.HasPermission(user,x.JobId,"ViewJob")).ToList())).RequireAuthorization();

        app.MapPost("/api/jobs/{jobId:guid}/operations/activities", (Guid jobId,JobActivityRequest request,ClaimsPrincipal user,WorkflowStore workflow,StudioDatabase database) => WorkflowEndpoints.Run(()=>
        {
            if(request.ExpectedRevision is null||request.OperationId==Guid.Empty)throw new ArgumentException("Refresh the Job before creating a task.");
            workflow.Execute(jobId,new("task",request.ExpectedRevision.Value,request.OperationId,UserName:request.AssignedTo,Title:request.Title,TaskId:request.OperationId,Kind:request.ActivityType,Priority:request.Priority,ScheduledAtUtc:request.ScheduledAtUtc,DueAtUtc:request.DueAtUtc,Description:request.Description,Location:request.Location,HandoffTo:request.HandoffTo),user);
            return Results.Ok(new OperationalWorkStore(database).GetActivities(jobId).Single(x=>x.Id==request.OperationId));
        })).RequireAuthorization();
        app.MapPut("/api/operations/activities/{id:guid}", (Guid id,JobActivityRequest request,ClaimsPrincipal user,WorkflowStore workflow,StudioDatabase database,IConfiguration configuration,IWebHostEnvironment environment) => WorkflowEndpoints.Run(()=>
        {
            var store=new OperationalWorkStore(database);var current=store.GetActivities().FirstOrDefault(x=>x.Id==id)??throw new KeyNotFoundException();
            if(request.ExpectedRevision is null||request.OperationId==Guid.Empty)throw new ArgumentException("Refresh the task before saving.");
            bool metadata=current.Title!=request.Title||current.ActivityType!=(request.ActivityType??current.ActivityType)||current.AssignedTo!=request.AssignedTo||current.Description!=request.Description||current.Priority!=(request.Priority??current.Priority)||current.ScheduledAtUtc!=request.ScheduledAtUtc||current.DueAtUtc!=request.DueAtUtc||current.Location!=request.Location||current.HandoffTo!=request.HandoffTo;
            if(metadata)
            {
                // A metadata edit and a progress edit are distinct versioned decisions.
                if(current.Status!=request.Status)throw new ArgumentException("Save task assignment/details before changing progress.");
                workflow.Execute(current.JobId,new("task",request.ExpectedRevision.Value,request.OperationId,UserName:request.AssignedTo,Title:request.Title,TaskId:id,Kind:request.ActivityType,Priority:request.Priority,ScheduledAtUtc:request.ScheduledAtUtc,DueAtUtc:request.DueAtUtc,Description:request.Description,Location:request.Location,HandoffTo:request.HandoffTo),user);
            }
            else
            {
                if(request.Status=="Completed"&&current.ActivityType is "FileIngest" or "Backup")
                {
                    var records=new MediaArchiveStore(database,configuration,environment).GetForJob(current.JobId);
                    if(current.ActivityType=="FileIngest"?!records.Any(x=>x.PrimaryVerifiedAtUtc.HasValue&&x.FileCount>0):!records.Any(x=>x.BackupVerifiedAtUtc.HasValue&&x.FileCount>0))throw new ArgumentException("Verify the configured media archive/backup before completing this task.");
                }
                workflow.Execute(current.JobId,new("task-progress",request.ExpectedRevision.Value,request.OperationId,TaskId:id,Status:request.Status,BlockedReason:request.BlockedReason),user);
            }
            return Results.Ok(store.GetActivities(current.JobId).Single(x=>x.Id==id));
        })).RequireAuthorization();

        app.MapPost("/api/jobs/{jobId:guid}/operations/template/{templateName}", (Guid jobId,string templateName,ClaimsPrincipal user,StudioDatabase database,JobAccessService access) =>
        {
            if(!new GlobalOperationalRoleStore(database).HasRole(user,StudioRoleNames.ProductionManager)) return Results.Forbid();
            OperationalWorkStore store=new(database); if(store.GetActivities(jobId).Count>0) return Results.Ok(store.GetActivities(jobId));
            (string Type,string Title)[] steps=templateName.Trim().ToLowerInvariant() switch
            {
                "graduation"=>[("FileIngest","Import images"),("Backup","Verify backup"),("Cull","Cull images"),("Edit","Color correction"),("Retouch","Retouch selections"),("Proof","Prepare proofs"),("QC","Quality control"),("Delivery","Final export and delivery"),("Archive","Archive deliverables")],
                "id" or "idphotos"=>[("FileIngest","Import images"),("Backup","Verify backup"),("Edit","Match and crop to specification"),("QC","Quality check"),("Delivery","Export final files"),("Archive","Archive deliverables")],
                _=>[("FileIngest","Import images"),("Backup","Verify backup"),("Cull","Cull and select"),("Edit","Edit and color correct"),("Retouch","Retouch selections"),("Proof","Client proof review"),("QC","Final quality control"),("Delivery","Deliver final files"),("Archive","Archive deliverables")]
            };
            foreach((string type,string title) in steps) store.CreateActivity(jobId,new JobActivityRequest(type,title,null,"NotStarted","Normal",null,null,null,null,null,null),UserName(user));
            return Results.Ok(store.GetActivities(jobId));
        }).RequireAuthorization();

        app.MapGet("/api/operations/shoots", (Guid? jobId,ClaimsPrincipal user,StudioDatabase database,JobAccessService access) => Results.Ok(new OperationalWorkStore(database).GetShoots(jobId).Where(x=>access.HasPermission(user,x.JobId,"ViewJob")).ToList())).RequireAuthorization();

        app.MapPost("/api/jobs/{jobId:guid}/operations/shoots", (Guid jobId,IntegratedShootRequest request,ClaimsPrincipal user,StudioDatabase database,JobAccessService access,NotificationService notifications,JobEventNotifier notifier) =>
        {
            if(!access.HasPermission(user,jobId,"ManageSchedule")&&!access.HasPermission(user,jobId,"AssignStaff")) return Results.Forbid();
            try
            {
                ShootCoordinator coordinator=new(database,new PhotographerStore(database));
                ShootSession shoot=coordinator.Create(jobId,request,UserName(user));


                notifications.PublishToUser(shoot.PhotographerUserName,"JobAssigned","Photography assignment",$"Call time {shoot.CallTimeUtc:u}{(shoot.Location is null?string.Empty:$" at {shoot.Location}")}.","Job",jobId.ToString("D"));
                notifier.Publish(jobId,"ScheduleChanged","Photography scheduled",$"Photography is scheduled for {shoot.CallTimeUtc:u} to {shoot.PlannedEndUtc:u}{(string.IsNullOrWhiteSpace(shoot.Location)?string.Empty:$" at {shoot.Location}")}.",requesterEligible:true);
                return Results.Created($"/api/operations/shoots/{shoot.Id}",shoot);
            }
            catch(Exception exception){ return Results.BadRequest(new { error=exception.Message }); }
        }).RequireAuthorization();

        app.MapGet("/api/jobs/{jobId:guid}/scheduling/availability",(Guid jobId,Guid? shootId,DateTimeOffset from,DateTimeOffset to,ClaimsPrincipal user,StudioDatabase db,JobAccessService access)=>
        {
            if(!access.HasPermission(user,jobId,"ManageSchedule")&&!access.HasPermission(user,jobId,"AssignStaff"))return Results.Forbid();
            if(to<=from||(to-from).TotalDays>7)return Results.BadRequest(new{error="Choose a scheduling window of up to seven days."});
            using var c=db.OpenConnection();using var cmd=new NpgsqlCommand("""
                select p.user_name,p.display_name,p.schedule_resource_id,
                  not exists(select 1 from studio_photographer_availability a where lower(a.user_name)=lower(p.user_name) and a.availability_type in ('Unavailable','Leave') and a.starts_at_utc<$2 and a.ends_at_utc>$1)
                  and not exists(select 1 from studio_schedule_entry_resources er join studio_schedule_entries e on e.id=er.entry_id where er.resource_id=p.schedule_resource_id and e.status<>'Cancelled' and e.starts_at_utc<$2 and e.ends_at_utc>$1
                    and not exists(select 1 from studio_shoot_sessions s where s.schedule_entry_id=e.id and s.id=$3 and s.job_id=$4))
                from studio_photographers p where p.is_active order by p.display_name
                """,c);
            foreach(var value in new object?[]{from.ToUniversalTime(),to.ToUniversalTime(),shootId,jobId})cmd.Parameters.AddWithValue(value??DBNull.Value);
            using var r=cmd.ExecuteReader();var rows=new List<object>();while(r.Read())rows.Add(new{userName=r.GetString(0),displayName=r.GetString(1),resourceId=r.GetGuid(2),available=r.GetBoolean(3)});return Results.Ok(rows);
        }).RequireAuthorization();

        app.MapPut("/api/jobs/{jobId:guid}/operations/shoots/{shootId:guid}/schedule",(Guid jobId,Guid shootId,IntegratedShootRequest request,ClaimsPrincipal user,
            Aegis.Studio.Web.WorkDay.IWorkDaySchedulingService scheduling,JobAccessService access,JobEventNotifier notifier)=>
        {
            if(!access.HasPermission(user,jobId,"ManageSchedule")&&!access.HasPermission(user,jobId,"AssignStaff"))return Results.Forbid();
            try
            {
                var shoot=scheduling.Reschedule(jobId,shootId,request,UserName(user));


                try{notifier.Publish(jobId,"JobAssigned","Shoot rescheduled",$"{shoot.PhotographerUserName}, {shoot.CallTimeUtc:u}");}
                catch(Exception notificationError){app.Logger.LogWarning(notificationError,"Shoot {ShootId} rescheduled; optional notification failed",shootId);}
                return Results.Ok(shoot);
            }
            catch(KeyNotFoundException){return Results.NotFound();}
            catch(InvalidOperationException e){return Results.Conflict(new{error=e.Message});}
        }).RequireAuthorization();

        app.MapPut("/api/operations/shoots/{id:guid}", (Guid id,ShootSessionUpdateRequest request,ClaimsPrincipal user,StudioDatabase database,JobAccessService access,NotificationService notifications,IConfiguration configuration,IWebHostEnvironment environment,JobEventNotifier notifier) =>
        {
            OperationalWorkStore store=new(database); ShootSession? current=store.GetShoots().FirstOrDefault(x=>x.Id==id); if(current is null) return Results.NotFound(); string currentUser=UserName(user); bool photographer=string.Equals(current.PhotographerUserName,currentUser,StringComparison.OrdinalIgnoreCase); if(!photographer&&!access.HasPermission(user,current.JobId,"ManageProduction")&&!access.HasPermission(user,current.JobId,"ManageSchedule")) return Results.Forbid();
            try
            {
                MediaArchiveStore media=new(database,configuration,environment);
                IReadOnlyList<MediaArchiveRecord> archives=media.GetForJob(current.JobId).Where(x=>x.ShootId==id).ToList();
                int verifiedFiles=archives.Where(x=>x.PrimaryVerifiedAtUtc.HasValue).Sum(x=>x.FileCount);
                bool verifiedBackup=archives.Any(x=>x.BackupVerifiedAtUtc.HasValue);
                ShootSessionUpdateRequest factual=request with { ImageCount=verifiedFiles>0?verifiedFiles:request.ImageCount,BackupConfirmed=verifiedBackup };
                if(string.Equals(request.Action,"handoff",StringComparison.OrdinalIgnoreCase)&&verifiedFiles==0) return Results.BadRequest(new { error="Media must be registered and verified on the filesystem before handoff." });
                ShootSession updated=store.UpdateShoot(id,factual,currentUser);
                if(updated.ReshootRequired) notifications.PublishToUser(updated.PhotographerUserName,"JobStatusChanged","Reshoot required",updated.CoverageNotes??"This job requires a reshoot.","Job",updated.JobId.ToString("D"));
                if(updated.HandoffStatus=="HandedOff"&&!string.IsNullOrWhiteSpace(updated.HandoffTo)) notifications.PublishToUser(updated.HandoffTo,"JobAssigned","Files handed off to production",$"{updated.ImageCount} verified files are ready for production. Backup verified: {updated.BackupConfirmed}.","Job",updated.JobId.ToString("D"));
                if(updated.ReshootRequired) notifier.Publish(updated.JobId,"ProductionChanged","Reshoot required",updated.CoverageNotes??"A reshoot is required.");
                if(updated.HandoffStatus=="HandedOff") notifier.Publish(updated.JobId,"ProductionChanged","Shoot handed to production",$"{updated.ImageCount} verified files were handed to {updated.HandoffTo??"production"}. Backup verified: {updated.BackupConfirmed}.");
                return Results.Ok(updated);
            }
            catch(Exception exception){ return Results.BadRequest(new { error=exception.Message }); }
        }).RequireAuthorization();

        app.MapGet("/api/photographers", (StudioDatabase database) => Results.Ok(new PhotographerStore(database).GetAll())).RequireAuthorization();
        app.MapPost("/api/photographers", (PhotographerProfileRequest request,ClaimsPrincipal user,StudioDatabase database) => { if(!user.IsInRole("Administrator")) return Results.Forbid(); try{return Results.Ok(new PhotographerStore(database).Save(request));}catch(Exception exception){return Results.BadRequest(new{error=exception.Message});} }).RequireAuthorization();
        app.MapGet("/api/photographers/{userName}/availability", (string userName,DateTimeOffset? from,DateTimeOffset? to,StudioDatabase database) => Results.Ok(new PhotographerStore(database).GetAvailability(userName,from,to))).RequireAuthorization();
        app.MapPost("/api/photographers/{userName}/availability", (string userName,PhotographerAvailabilityRequest request,ClaimsPrincipal user,StudioDatabase database) => { if(!user.IsInRole("Administrator")&&!string.Equals(UserName(user),userName,StringComparison.OrdinalIgnoreCase)) return Results.Forbid(); try{return Results.Ok(new PhotographerStore(database).AddAvailability(userName,request));}catch(Exception exception){return Results.BadRequest(new{error=exception.Message});} }).RequireAuthorization();

        app.MapGet("/api/jobs/{jobId:guid}/media-archives", (Guid jobId,ClaimsPrincipal user,JobAccessService access,StudioDatabase database,IConfiguration configuration,IWebHostEnvironment environment) => access.HasPermission(user,jobId,"ViewJob")?Results.Ok(new MediaArchiveStore(database,configuration,environment).GetForJob(jobId)):Results.Forbid()).RequireAuthorization();
        app.MapPost("/api/jobs/{jobId:guid}/media-archives", (Guid jobId,MediaArchiveRegistrationRequest request,ClaimsPrincipal user,JobAccessService access,StudioDatabase database,IConfiguration configuration,IWebHostEnvironment environment) => { if(!access.HasPermission(user,jobId,"ManageProduction")) return Results.Forbid(); try{return Results.Ok(new MediaArchiveStore(database,configuration,environment).Register(jobId,request));}catch(Exception exception){return Results.BadRequest(new{error=exception.Message});} }).RequireAuthorization();
        app.MapPost("/api/jobs/{jobId:guid}/media-archives/{id:guid}/verify", (Guid jobId,Guid id,ClaimsPrincipal user,JobAccessService access,StudioDatabase database,IConfiguration configuration,IWebHostEnvironment environment) => { if(!access.HasPermission(user,jobId,"ManageProduction")) return Results.Forbid(); try{MediaArchiveRecord value=new MediaArchiveStore(database,configuration,environment).Verify(id); return value.JobId==jobId?Results.Ok(value):Results.NotFound();}catch(Exception exception){return Results.BadRequest(new{error=exception.Message});} }).RequireAuthorization();

        app.MapGet("/api/workspaces/{workspaceId:guid}/performance", (
            Guid workspaceId,
            DateTimeOffset? from,
            DateTimeOffset? to,
            ClaimsPrincipal user,
            WorkspaceAccessService workspaceAccess,
            PerformanceMetricsStore metrics) =>
        {
            if(!user.IsInRole("Administrator") &&
               !workspaceAccess.HasPermission(user,workspaceId,"ManageProduction") &&
               !workspaceAccess.HasPermission(user,workspaceId,"ManageSchedule"))
                return Results.Forbid();

            DateTimeOffset end=(to??DateTimeOffset.UtcNow).ToUniversalTime();
            DateTimeOffset start=(from??end.AddDays(-30)).ToUniversalTime();
            if(end<=start) return Results.BadRequest(new{error="Performance window end must be after start."});

            try{return Results.Ok(metrics.GetWorkspacePerformance(workspaceId,start,end));}
            catch(InvalidOperationException ex){return Results.BadRequest(new{error=ex.Message});}
        }).RequireAuthorization();

        app.MapGet("/api/jobs/{jobId:guid}/operations/audit", (Guid jobId,ClaimsPrincipal user,StudioDatabase database,JobAccessService access) => access.HasPermission(user,jobId,"ViewJob")?Results.Ok(new OperationalWorkStore(database).GetAudit(jobId)):Results.Forbid()).RequireAuthorization();
    }

    private static string UserName(ClaimsPrincipal user)=>user.FindFirst("login_name")?.Value??user.Identity?.Name??"Studio";
}

