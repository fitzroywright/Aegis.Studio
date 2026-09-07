using System.Security.Claims;
using Aegis.Studio.Web.Notifications;
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

        app.MapGet("/api/operations/activities", (Guid? jobId,string? status,string? assignedTo,ClaimsPrincipal user,StudioDatabase database,JobAccessService access) => Results.Ok(new OperationalWorkStore(database).GetActivities(jobId,status,assignedTo).Where(x=>access.HasPermission(user,x.JobId,"ViewJob")).ToList())).RequireAuthorization();

        app.MapPost("/api/jobs/{jobId:guid}/operations/activities", (Guid jobId,JobActivityRequest request,ClaimsPrincipal user,StudioDatabase database,JobAccessService access,NotificationService notifications) =>
        {
            if(!access.HasPermission(user,jobId,"ManageProduction")) return Results.Forbid();
            try { OperationalWorkStore store=new(database); JobActivity activity=store.CreateActivity(jobId,request,UserName(user)); if(!string.IsNullOrWhiteSpace(activity.AssignedTo)){ access.EnsureAssignedMember(jobId,activity.AssignedTo); notifications.PublishToUser(activity.AssignedTo,"JobAssigned",$"Activity assigned: {activity.Title}",$"You were assigned {activity.ActivityType} work.","Job",jobId.ToString("D")); } return Results.Created($"/api/operations/activities/{activity.Id}",activity); }
            catch(Exception exception){ return Results.BadRequest(new { error=exception.Message }); }
        }).RequireAuthorization();

        app.MapPut("/api/operations/activities/{id:guid}", (Guid id,JobActivityRequest request,ClaimsPrincipal user,StudioDatabase database,JobAccessService access,NotificationService notifications,IConfiguration configuration,IWebHostEnvironment environment) =>
        {
            OperationalWorkStore store=new(database); JobActivity? current=store.GetActivities().FirstOrDefault(x=>x.Id==id); if(current is null) return Results.NotFound(); if(!access.HasPermission(user,current.JobId,"ManageProduction")) return Results.Forbid();
            try
            {
                if(string.Equals(request.Status,"Completed",StringComparison.OrdinalIgnoreCase) && (current.ActivityType=="FileIngest" || current.ActivityType=="Backup"))
                {
                    MediaArchiveStore media=new(database,configuration,environment);
                    IReadOnlyList<MediaArchiveRecord> records=media.GetForJob(current.JobId);
                    bool primary=records.Any(x=>x.PrimaryVerifiedAtUtc.HasValue&&x.FileCount>0);
                    bool backup=records.Any(x=>x.BackupVerifiedAtUtc.HasValue&&x.FileCount>0);
                    if(current.ActivityType=="FileIngest"&&!primary) return Results.BadRequest(new { error="File ingest cannot be completed until Studio verifies files in the configured media archive." });
                    if(current.ActivityType=="Backup"&&!backup) return Results.BadRequest(new { error="Backup cannot be completed until Studio verifies the backup directory." });
                }
                JobActivity updated=store.UpdateActivity(id,request,UserName(user)); if(updated.Status=="Blocked") notifications.PublishToUser(updated.AssignedTo??UserName(user),"JobStatusChanged",$"Blocked: {updated.Title}",updated.BlockedReason??"Production activity is blocked.","Job",updated.JobId.ToString("D")); return Results.Ok(updated);
            }
            catch(Exception exception){ return Results.BadRequest(new { error=exception.Message }); }
        }).RequireAuthorization();

        app.MapPost("/api/jobs/{jobId:guid}/operations/template/{templateName}", (Guid jobId,string templateName,ClaimsPrincipal user,StudioDatabase database,JobAccessService access) =>
        {
            if(!access.HasPermission(user,jobId,"ManageProduction")) return Results.Forbid();
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

        app.MapPost("/api/jobs/{jobId:guid}/operations/shoots", (Guid jobId,IntegratedShootRequest request,ClaimsPrincipal user,StudioDatabase database,JobAccessService access,NotificationService notifications) =>
        {
            if(!access.HasPermission(user,jobId,"ManageSchedule")&&!access.HasPermission(user,jobId,"AssignStaff")) return Results.Forbid();
            try { ShootCoordinator coordinator=new(database,new PhotographerStore(database)); ShootSession shoot=coordinator.Create(jobId,request,UserName(user)); access.EnsureAssignedMember(jobId,shoot.PhotographerUserName); if(!string.IsNullOrWhiteSpace(shoot.AssistantUserName)) access.EnsureAssignedMember(jobId,shoot.AssistantUserName); notifications.PublishToUser(shoot.PhotographerUserName,"JobAssigned","Photography assignment",$"Call time {shoot.CallTimeUtc:u}{(shoot.Location is null?string.Empty:$" at {shoot.Location}")}.","Job",jobId.ToString("D")); return Results.Created($"/api/operations/shoots/{shoot.Id}",shoot); }
            catch(Exception exception){ return Results.BadRequest(new { error=exception.Message }); }
        }).RequireAuthorization();

        app.MapPut("/api/operations/shoots/{id:guid}", (Guid id,ShootSessionUpdateRequest request,ClaimsPrincipal user,StudioDatabase database,JobAccessService access,NotificationService notifications,IConfiguration configuration,IWebHostEnvironment environment) =>
        {
            OperationalWorkStore store=new(database); ShootSession? current=store.GetShoots().FirstOrDefault(x=>x.Id==id); if(current is null) return Results.NotFound(); string currentUser=UserName(user); bool photographer=string.Equals(current.PhotographerUserName,currentUser,StringComparison.OrdinalIgnoreCase); if(!photographer&&!access.HasPermission(user,current.JobId,"ManageProduction")&&!access.HasPermission(user,current.JobId,"ManageSchedule")) return Results.Forbid();
            try { MediaArchiveStore media=new(database,configuration,environment); IReadOnlyList<MediaArchiveRecord> archives=media.GetForJob(current.JobId).Where(x=>x.ShootId==id).ToList(); int verifiedFiles=archives.Where(x=>x.PrimaryVerifiedAtUtc.HasValue).Sum(x=>x.FileCount); bool verifiedBackup=archives.Any(x=>x.BackupVerifiedAtUtc.HasValue); ShootSessionUpdateRequest factual=request with { ImageCount=verifiedFiles>0?verifiedFiles:request.ImageCount,BackupConfirmed=verifiedBackup }; if(string.Equals(request.Action,"handoff",StringComparison.OrdinalIgnoreCase)&&verifiedFiles==0) return Results.BadRequest(new { error="Media must be registered and verified on the filesystem before handoff." }); ShootSession updated=store.UpdateShoot(id,factual,currentUser); if(updated.ReshootRequired) notifications.PublishToUser(updated.PhotographerUserName,"JobStatusChanged","Reshoot required",updated.CoverageNotes??"This job requires a reshoot.","Job",updated.JobId.ToString("D")); if(updated.HandoffStatus=="HandedOff"&&!string.IsNullOrWhiteSpace(updated.HandoffTo)) notifications.PublishToUser(updated.HandoffTo,"JobAssigned","Files handed off to production",$"{updated.ImageCount} verified files are ready for production. Backup verified: {updated.BackupConfirmed}.","Job",updated.JobId.ToString("D")); return Results.Ok(updated); }
            catch(Exception exception){ return Results.BadRequest(new { error=exception.Message }); }
        }).RequireAuthorization();

        app.MapGet("/api/photographers", (StudioDatabase database) => Results.Ok(new PhotographerStore(database).GetAll())).RequireAuthorization();
        app.MapPost("/api/photographers", (PhotographerProfileRequest request,ClaimsPrincipal user,StudioDatabase database) => { if(!user.IsInRole("Administrator")) return Results.Forbid(); try{return Results.Ok(new PhotographerStore(database).Save(request));}catch(Exception exception){return Results.BadRequest(new{error=exception.Message});} }).RequireAuthorization();
        app.MapGet("/api/photographers/{userName}/availability", (string userName,DateTimeOffset? from,DateTimeOffset? to,StudioDatabase database) => Results.Ok(new PhotographerStore(database).GetAvailability(userName,from,to))).RequireAuthorization();
        app.MapPost("/api/photographers/{userName}/availability", (string userName,PhotographerAvailabilityRequest request,ClaimsPrincipal user,StudioDatabase database) => { if(!user.IsInRole("Administrator")&&!string.Equals(UserName(user),userName,StringComparison.OrdinalIgnoreCase)) return Results.Forbid(); try{return Results.Ok(new PhotographerStore(database).AddAvailability(userName,request));}catch(Exception exception){return Results.BadRequest(new{error=exception.Message});} }).RequireAuthorization();

        app.MapGet("/api/jobs/{jobId:guid}/media-archives", (Guid jobId,ClaimsPrincipal user,JobAccessService access,StudioDatabase database,IConfiguration configuration,IWebHostEnvironment environment) => access.HasPermission(user,jobId,"ViewJob")?Results.Ok(new MediaArchiveStore(database,configuration,environment).GetForJob(jobId)):Results.Forbid()).RequireAuthorization();
        app.MapPost("/api/jobs/{jobId:guid}/media-archives", (Guid jobId,MediaArchiveRegistrationRequest request,ClaimsPrincipal user,JobAccessService access,StudioDatabase database,IConfiguration configuration,IWebHostEnvironment environment) => { if(!access.HasPermission(user,jobId,"ManageProduction")) return Results.Forbid(); try{return Results.Ok(new MediaArchiveStore(database,configuration,environment).Register(jobId,request));}catch(Exception exception){return Results.BadRequest(new{error=exception.Message});} }).RequireAuthorization();
        app.MapPost("/api/jobs/{jobId:guid}/media-archives/{id:guid}/verify", (Guid jobId,Guid id,ClaimsPrincipal user,JobAccessService access,StudioDatabase database,IConfiguration configuration,IWebHostEnvironment environment) => { if(!access.HasPermission(user,jobId,"ManageProduction")) return Results.Forbid(); try{MediaArchiveRecord value=new MediaArchiveStore(database,configuration,environment).Verify(id); return value.JobId==jobId?Results.Ok(value):Results.NotFound();}catch(Exception exception){return Results.BadRequest(new{error=exception.Message});} }).RequireAuthorization();

        app.MapGet("/api/jobs/{jobId:guid}/operations/audit", (Guid jobId,ClaimsPrincipal user,StudioDatabase database,JobAccessService access) => access.HasPermission(user,jobId,"ViewJob")?Results.Ok(new OperationalWorkStore(database).GetAudit(jobId)):Results.Forbid()).RequireAuthorization();
    }

    private static string UserName(ClaimsPrincipal user)=>user.FindFirst("login_name")?.Value??user.Identity?.Name??"Studio";
}
