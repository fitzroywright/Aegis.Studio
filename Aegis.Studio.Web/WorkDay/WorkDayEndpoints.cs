using System.Security.Claims;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Production;
using Aegis.Studio.Web.Scheduling;
using Aegis.Studio.Web.Storage;

namespace Aegis.Studio.Web.WorkDay;

public static class WorkDayEndpoints
{
    public static void MapStudioWorkDay(this WebApplication app)
    {
        RouteGroupBuilder group=app.MapGroup("/api/workday").RequireAuthorization();

        group.MapPost("/device-session",(ClaimsPrincipal user,WorkDayDeviceSessionRequest request,WorkDayStore store,StudioDatabase db,IConfiguration configuration)=>
        {
            if(string.Equals(user.Identity?.AuthenticationType,"WorkDayDeviceToken",StringComparison.OrdinalIgnoreCase))
                return Results.Forbid();
            if(string.IsNullOrWhiteSpace(request.DeviceId)) return Results.BadRequest(new{error="DeviceId is required."});
            if(new PhotographerStore(db).Get(UserName(user)) is not { IsActive:true })return Results.Forbid();
            int days=Math.Clamp(configuration.GetValue<int?>("WorkDay:DeviceSessionDays")??30,1,90);
            var issued=store.IssueDeviceSession(UserName(user),request.DeviceId,TimeSpan.FromDays(days));
            return Results.Ok(new WorkDayDeviceSessionResponse(issued.SessionId,issued.Token,issued.ExpiresAtUtc));
        });

        group.MapPost("/device-session/revoke",(ClaimsPrincipal user,WorkDayDeviceSessionRequest request,WorkDayStore store)=>
        {
            if(string.IsNullOrWhiteSpace(request.DeviceId)) return Results.BadRequest(new{error="DeviceId is required."});
            store.RevokeDeviceSession(UserName(user),request.DeviceId);
            return Results.NoContent();
        });

        group.MapGet("/bootstrap",(ClaimsPrincipal user,StudioDatabase db,ScheduleStore schedules,WorkDayStore store,IConfiguration configuration,JobAccessService access)=>
        {
            string userName=UserName(user);
            PhotographerProfile? profile=new PhotographerStore(db).Get(userName);
            if(profile is null || !profile.IsActive) return Results.NotFound(new{error="No active Studio field profile is linked to this account."});

            DateTimeOffset now=DateTimeOffset.UtcNow;
            TimeZoneInfo zone=TimeZoneInfo.FindSystemTimeZoneById(configuration["Studio:TimeZoneId"]??"America/Jamaica");
            DateTime localDate=TimeZoneInfo.ConvertTime(now,zone).Date;
            DateTimeOffset todayStart=new DateTimeOffset(localDate,zone.GetUtcOffset(localDate)).ToUniversalTime();
            DateTimeOffset todayEnd=todayStart.AddDays(1);
            WorkDayAssignment[] all=schedules.GetEntries(todayStart.AddDays(-1),todayEnd.AddDays(30))
                .Where(x=>x.ResourceIds.Contains(profile.ScheduleResourceId))
                .Select(x=>EnsureAndMapAssignmentAccess(user,profile,x,db,store,access))
                .Where(x=>x is not null)
                .Select(x=>x!)
                .ToArray();

            using var rosterConnection=db.OpenConnection();
            var fieldTargets=all.ToDictionary(x=>x.Id,x=>WorkDayFieldReports.Targets(rosterConnection,null,x.Id));
            return Results.Ok(new WorkDayBootstrap(
                userName,
                profile.DisplayName,
                now,
                all.Where(x=>x.StartsAtUtc<todayEnd&&x.EndsAtUtc>todayStart).ToArray(),
                all.Where(x=>x.StartsAtUtc>=todayEnd).ToArray(),
                store.GetActiveSession(userName),
                configuration.GetValue<bool?>("WorkDay:LocationTrackingEnabled") ?? false,
                (configuration.GetValue<bool?>("WorkDay:LocationTrackingEnabled") ?? false)
                    ? ParseTrackingMode(configuration["WorkDay:LocationTrackingMode"])
                    : WorkDayLocationTrackingMode.Disabled,
                now,fieldTargets));
        });

        group.MapGet("/availability",(ClaimsPrincipal user,DateTimeOffset? from,DateTimeOffset? to,StudioDatabase db)=>
        {
            string userName=UserName(user);
            PhotographerStore photographers=new(db);
            if(photographers.Get(userName) is not { IsActive:true }) return Results.NotFound(new{error="No active Studio field profile is linked to this account."});
            return Results.Ok(photographers.GetAvailability(userName,from,to));
        });

        group.MapPost("/availability",(ClaimsPrincipal user,PhotographerAvailabilityRequest request,StudioDatabase db)=>
        {
            string userName=UserName(user);
            PhotographerStore photographers=new(db);
            try{return Results.Created("/api/workday/availability",photographers.AddAvailability(userName,request));}
            catch(KeyNotFoundException){return Results.NotFound(new{error="No active Studio field profile is linked to this account."});}
            catch(InvalidOperationException ex){return Results.BadRequest(new{error=ex.Message});}
        });

        group.MapPost("/operations",(ClaimsPrincipal user,WorkDayOperationRequest request,StudioDatabase db,ScheduleStore schedules,WorkDayStore store,StudioOperationsFlowPublisher flows,JobAccessService access,IConfiguration configuration)=>
        {
            string userName=UserName(user);
            if(!WorkDaySemantics.IsPhotographerOperation(request.Kind)||request.AssignmentId is null)
                return Results.BadRequest(new{error="WorkDay reports photographer assignments; a supported action and assignment are required."});
            if(user.Identity?.AuthenticationType=="WorkDayDeviceToken"&&user.FindFirstValue("workday_device_id")!=request.DeviceId)
                return Results.Forbid();
            bool locationEnabled=configuration.GetValue<bool?>("WorkDay:LocationTrackingEnabled") ?? false;
            if(!locationEnabled && request.Kind==WorkDayOperationKind.LocationObservation)
                return Results.BadRequest(new{error="WorkDay location tracking is disabled."});
            if(!locationEnabled && request.Location is not null)
                request=request with { Location=null };

            // Apply verifies the active reporter inside its Job/schedule transaction.
            // Replay is checked first so a lost acknowledgement remains recoverable
            // after the photographer is reassigned.

            try
            {
                string correlation=request.OperationId.ToString("D");
                flows.TryEnqueueWorkDaySync(correlation,"ServerProcessing",request.AssignmentId?.ToString("D"));
                WorkDayOperationResult result=store.Apply(userName,request);
                if(result.State==WorkDayOperationState.Conflict)
                {
                    flows.TryEnqueueWorkDaySync(correlation,"ServerProcessing",request.AssignmentId?.ToString("D"),result.Message,failed:true);
                    return Results.Conflict(result);
                }

                flows.TryEnqueueWorkDaySync(correlation,"Acknowledged",request.AssignmentId?.ToString("D"));
                if(request.AssignmentId is Guid businessId)
                {
                    string? stage=request.Kind switch
                    {
                        WorkDayOperationKind.AcceptAssignment=>"Accepted",
                        WorkDayOperationKind.Arrive=>"Arrived",
                        WorkDayOperationKind.StartSession=>"InProgress",
                        WorkDayOperationKind.CompleteSession=>"Completed",
                        _=>null
                    };
                    if(stage is not null)
                    {
                        flows.TryEnqueueWorkDaySession(correlation,stage,businessId.ToString("D"));
                        ScheduleEntry? flowEntry=schedules.GetEntries(entryId:businessId).SingleOrDefault();
                        Guid? flowJobId=flowEntry is null?null:ResolveAssignmentJobId(flowEntry,db);
                        if(flowJobId is Guid jobId)
                        {
                            flows.TryEnqueueJobFlow(jobId.ToString("D"),stage,jobId.ToString("D"),$"WorkDay {request.Kind} acknowledged for assignment {businessId:D}.");
                            if(request.Kind==WorkDayOperationKind.CompleteSession)
                                flows.TryEnqueueJobFlow(jobId.ToString("D"),"Synchronized",jobId.ToString("D"),"Completion acknowledged by Studio.");
                        }
                    }
                }
                return Results.Ok(result);
            }
            catch(InvalidOperationException ex){return Results.BadRequest(new{error=ex.Message});}
        });

        group.MapGet("/work-session",(ClaimsPrincipal user,WorkDayStore store)=>
        {
            WorkDayWorkSession? session=store.GetActiveSession(UserName(user));
            return session is null?Results.NoContent():Results.Ok(session);
        });

        group.MapGet("/time-summary",(
            DateTimeOffset? from,
            DateTimeOffset? to,
            ClaimsPrincipal user,
            WorkDayStore store,
            IConfiguration configuration)=>
        {
            DateTimeOffset end=(to??DateTimeOffset.UtcNow).ToUniversalTime();
            DateTimeOffset start=(from??end.AddDays(-14)).ToUniversalTime();
            if(end<=start) return Results.BadRequest(new{error="Time summary end must be after start."});
            WorkDayTimePolicy policy=WorkDayTimeSemantics.FromConfiguration(configuration);
            return Results.Ok(store.GetTimeSummaries([UserName(user)],start,end,policy).Single());
        });

        group.MapGet("/diagnostics",(ClaimsPrincipal user,WorkDayStore store,IConfiguration configuration)=>
        {
            int seconds=Math.Clamp(configuration.GetValue<int?>("WorkDay:TelemetryStaleSeconds")??300,60,3600);
            return Results.Ok(store.GetDiagnostics(UserName(user),TimeSpan.FromSeconds(seconds)));
        });

        group.MapPost("/client-observation",(ClaimsPrincipal user,WorkDayClientObservation observation,WorkDayStore store)=>
        {
            string userName=UserName(user);
            string? tokenDevice=user.FindFirstValue("workday_device_id");
            if(!string.IsNullOrWhiteSpace(tokenDevice) &&
               !string.Equals(tokenDevice,observation.DeviceId,StringComparison.Ordinal))
                return Results.Forbid();
            store.ObserveClient(userName,observation);
            return Results.NoContent();
        });

        group.MapGet("/fleet",(ClaimsPrincipal user,WorkDayStore store,IConfiguration configuration)=>
        {
            if(!user.IsInRole("Administrator") &&
               !user.HasClaim("permission","Studio.WorkDay.Management.View") &&
               !user.HasClaim("capability","Studio.WorkDay.Management.View"))
                return Results.Forbid();
            int seconds=Math.Clamp(configuration.GetValue<int?>("WorkDay:TelemetryStaleSeconds")??300,60,3600);
            return Results.Ok(store.GetFleetSnapshot(TimeSpan.FromSeconds(seconds)));
        });

        group.MapGet("/management/config",(ClaimsPrincipal user,IConfiguration configuration)=>
        {
            if(!CanViewLocation(user)) return Results.Forbid();
            return Results.Ok(new
            {
                locationTrackingEnabled=configuration.GetValue<bool?>("WorkDay:LocationTrackingEnabled")??false
            });
        });

        group.MapPost("/assignments/{assignmentId:guid}/uploads", async (
            Guid assignmentId,
            HttpContext context,
            ClaimsPrincipal user,
            StudioDatabase db,
            ScheduleStore schedules,
            StudioStore studio,
            AttachmentStore attachments,
            JobAccessService access,
            StudioOperationsFlowPublisher flows,
            CancellationToken ct)=>
        {
            string userName=UserName(user);
            if(!AssignmentAllowed(user,userName,assignmentId,db,schedules,access,"AddAttachments")) return Results.Forbid();
            if(!context.Request.HasFormContentType) return Results.BadRequest(new{error="multipart/form-data is required."});

            IFormCollection form=await context.Request.ReadFormAsync(ct);
            if(form.Files.Count!=1) return Results.BadRequest(new{error="Exactly one file is required per upload operation."});
            if(!Guid.TryParse(form["uploadId"],out Guid uploadId) || uploadId==Guid.Empty) return Results.BadRequest(new{error="A stable uploadId is required."});

            ScheduleEntry? entry=schedules.GetEntries(DateTimeOffset.UtcNow.AddYears(-10),DateTimeOffset.UtcNow.AddYears(10)).FirstOrDefault(x=>x.Id==assignmentId);
            if(entry is null || ResolveAssignmentJobId(entry,db) is not Guid jobId)
                return Results.BadRequest(new{error="The assignment is not associated with a Studio job."});
            StudioJob? job=studio.GetJob(jobId);
            if(job is null) return Results.NotFound(new{error="The assignment job was not found."});

            string correlation=uploadId.ToString("D");
            IFormFile file=form.Files[0];
            try
            {
                flows.TryEnqueueWorkDayUpload(correlation,"Upload",assignmentId.ToString("D"));
                StudioAttachment stored=await attachments.AddAsync(job.ConversationId,file,"Internal",userName,ct,$"workday:{uploadId:D}");
                flows.TryEnqueueWorkDayUpload(correlation,"Storage",stored.Id.ToString("D"));
                flows.TryEnqueueWorkDayUpload(correlation,"MetadataAssociation",assignmentId.ToString("D"));
                flows.TryEnqueueWorkDayUpload(correlation,"Acknowledged",stored.Id.ToString("D"));
                return Results.Ok(new WorkDayUploadResult(uploadId,stored.Id,assignmentId,stored.FileName,stored.Size,stored.StorageStatus,DateTimeOffset.UtcNow));
            }
            catch(Exception ex) when(ex is InvalidOperationException or Common.Storage.StorageException)
            {
                flows.TryEnqueueWorkDayUpload(correlation,"Storage",assignmentId.ToString("D"),ex.GetType().Name,failed:true);
                return Results.BadRequest(new{error=ex.Message});
            }
        }).DisableAntiforgery();

        group.MapGet("/assignments/{assignmentId:guid}/gallery-status",(
            Guid assignmentId,
            ClaimsPrincipal user,
            StudioDatabase db,
            ScheduleStore schedules,
            JobAccessService access)=>
        {
            string userName=UserName(user);
            if(!AssignmentAllowed(user,userName,assignmentId,db,schedules,access,"ViewAttachments"))
                return Results.Forbid();

            ScheduleEntry? entry=schedules.GetEntries(DateTimeOffset.UtcNow.AddYears(-10),DateTimeOffset.UtcNow.AddYears(10))
                .FirstOrDefault(x=>x.Id==assignmentId);
            if(entry is null || ResolveAssignmentJobId(entry,db) is not Guid jobId)
                return Results.NotFound();

            using Npgsql.NpgsqlConnection connection=db.OpenConnection();
            using Npgsql.NpgsqlCommand job=connection.CreateCommand();
            job.CommandText="select workspace_id from studio_jobs where id=$1";
            job.Parameters.AddWithValue(jobId);
            if(job.ExecuteScalar() is not Guid workspaceId) return Results.NotFound();

            using Npgsql.NpgsqlCommand gallery=connection.CreateCommand();
            gallery.CommandText="""
                select g.id,g.title,g.is_published,g.published_at_utc,count(i.id)
                from studio_workspace_galleries g
                left join studio_gallery_items i on i.gallery_id=g.id and i.is_visible
                where g.workspace_id=$1
                group by g.id,g.title,g.is_published,g.published_at_utc
                """;
            gallery.Parameters.AddWithValue(workspaceId);
            using Npgsql.NpgsqlDataReader reader=gallery.ExecuteReader();
            if(!reader.Read())
                return Results.Ok(new { workspaceId, exists=false, title=(string?)null, isPublished=false, publishedAtUtc=(DateTimeOffset?)null, visibleItems=0L });

            return Results.Ok(new
            {
                workspaceId,
                exists=true,
                galleryId=reader.GetGuid(0),
                title=reader.GetString(1),
                isPublished=reader.GetBoolean(2),
                publishedAtUtc=reader.IsDBNull(3)?(DateTimeOffset?)null:reader.GetFieldValue<DateTimeOffset>(3),
                visibleItems=reader.GetInt64(4)
            });
        });

        group.MapGet("/workspaces/{workspaceId:guid}/activity",(
            Guid workspaceId,
            ClaimsPrincipal user,
            StudioDatabase db,
            ScheduleStore schedules,
            WorkDayStore store,
            WorkspaceAccessService workspaceAccess,
            IConfiguration configuration)=>
        {
            if(!user.IsInRole("Administrator") &&
               !workspaceAccess.HasPermission(user,workspaceId,"ManageProduction") &&
               !workspaceAccess.HasPermission(user,workspaceId,"ManageSchedule"))
                return Results.Forbid();

            PhotographerStore photographers=new(db);
            IReadOnlyList<PhotographerProfile> profiles=photographers.GetAll()
                .Where(x=>x.IsActive)
                .ToArray();

            using Npgsql.NpgsqlConnection connection=db.OpenConnection();
            using Npgsql.NpgsqlCommand jobs=connection.CreateCommand();
            jobs.CommandText="select id from studio_jobs where workspace_id=$1";
            jobs.Parameters.AddWithValue(workspaceId);
            using Npgsql.NpgsqlDataReader reader=jobs.ExecuteReader();
            HashSet<Guid> jobIds=[];
            while(reader.Read()) jobIds.Add(reader.GetGuid(0));
            reader.Close();

            DateTimeOffset from=DateTimeOffset.UtcNow.AddDays(-1);
            DateTimeOffset to=DateTimeOffset.UtcNow.AddDays(30);
            ScheduleEntry[] entries=schedules.GetEntries(from,to)
                .Where(x=>ResolveAssignmentJobId(x,db) is Guid jobId && jobIds.Contains(jobId))
                .ToArray();

            HashSet<Guid> assignedResourceIds=entries.SelectMany(x=>x.ResourceIds).ToHashSet();
            PhotographerProfile[] assignedProfiles=profiles.Where(x=>assignedResourceIds.Contains(x.ScheduleResourceId)).ToArray();
            IReadOnlyList<WorkDayClientStatus> clients=store.GetClientStatuses(assignedProfiles.Select(x=>x.UserName).ToArray());
            Dictionary<string,WorkDayClientStatus> latest=clients
                .GroupBy(x=>x.UserName,StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g=>g.Key,g=>g.OrderByDescending(x=>x.ObservedAtUtc).First(),StringComparer.OrdinalIgnoreCase);
            Dictionary<Guid,StaffCapacitySnapshot> capacity=schedules.GetStaffCapacity(DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddDays(7))
                .ToDictionary(x=>x.ScheduleResourceId);
            WorkDayTimePolicy timePolicy=WorkDayTimeSemantics.FromConfiguration(configuration);
            DateTimeOffset timeFrom=DateTimeOffset.UtcNow.AddDays(-14);
            DateTimeOffset timeTo=DateTimeOffset.UtcNow;
            Dictionary<string,WorkDayTimeSummary> timeSummaries=store
                .GetTimeSummaries(assignedProfiles.Select(x=>x.UserName).ToArray(),timeFrom,timeTo,timePolicy)
                .ToDictionary(x=>x.UserName,StringComparer.OrdinalIgnoreCase);

            var activity=assignedProfiles.Select(profile=>
            {
                WorkDayAssignment[] assignments=entries
                    .Where(x=>x.ResourceIds.Contains(profile.ScheduleResourceId))
                    .Select(x=>MapAssignment(x,db,store,profile.UserName))
                    .ToArray();
                latest.TryGetValue(profile.UserName,out WorkDayClientStatus? client);
                capacity.TryGetValue(profile.ScheduleResourceId,out StaffCapacitySnapshot? cap);
                timeSummaries.TryGetValue(profile.UserName,out WorkDayTimeSummary? timeSummary);
                return new WorkspacePhotographerActivity(
                    profile.UserName,
                    profile.DisplayName,
                    profile.ScheduleResourceId,
                    client,
                    store.GetActiveSession(profile.UserName),
                    assignments,
                    cap,
                    timeSummary);
            }).OrderBy(x=>x.DisplayName,StringComparer.OrdinalIgnoreCase).ToArray();

            return Results.Ok(new WorkspaceWorkDayActivity(workspaceId,DateTimeOffset.UtcNow,activity));
        });

        group.MapGet("/management/sessions",(string? userName,int? count,ClaimsPrincipal user,WorkDayStore store)=>
        {
            if(!CanViewLocation(user)) return Results.Forbid();
            return Results.Ok(store.GetWorkSessions(string.IsNullOrWhiteSpace(userName)?null:userName.Trim(),count??100));
        });

        group.MapGet("/work-sessions/{workSessionId:guid}/timeline",(Guid workSessionId,string? userName,ClaimsPrincipal user,WorkDayStore store,IConfiguration configuration)=>
        {
            if(!CanViewLocation(user)) return Results.Forbid();
            string target=string.IsNullOrWhiteSpace(userName)?UserName(user):userName.Trim();
            WorkDayWorkSession? session=store.GetWorkSessions(target,500).FirstOrDefault(x=>x.Id==workSessionId);
            if(session is null) return Results.NotFound();

            bool locationEnabled=configuration.GetValue<bool?>("WorkDay:LocationTrackingEnabled") ?? false;
            if(!locationEnabled)
                return Results.Ok(new WorkDayTimeline(workSessionId,target,session.StartedAtUtc,session.EndedAtUtc,[]));

            store.AuditLocationAccess(UserName(user),target,workSessionId,"ViewTimeline");
            IReadOnlyList<WorkDayLocationEvidence> observations=store.GetLocationTimeline(target,workSessionId);
            return Results.Ok(new WorkDayTimeline(workSessionId,target,session.StartedAtUtc,session.EndedAtUtc,observations));
        });

        group.MapPost("/management/sessions/{workSessionId:guid}/corrections",(Guid workSessionId,WorkDaySessionCorrectionRequest request,ClaimsPrincipal user,WorkDayStore store)=>
        {
            if(!CanCorrectWorkSession(user)) return Results.Forbid();
            try{return Results.Ok(store.CorrectWorkSession(workSessionId,request,UserName(user)));}
            catch(KeyNotFoundException){return Results.NotFound();}
            catch(InvalidOperationException ex){return Results.BadRequest(new{error=ex.Message});}
        });

        app.MapGet("/workspaces/{workspaceId:guid}/workday-management",(Guid workspaceId,ClaimsPrincipal user,WorkspaceAccessService access,IWebHostEnvironment environment)=>
        {
            if(!user.IsInRole("Administrator") &&
               !access.HasPermission(user,workspaceId,"ManageProduction") &&
               !access.HasPermission(user,workspaceId,"ManageSchedule"))
                return Results.Forbid();

            return Results.File(
                Path.Combine(environment.WebRootPath,"workday-workspace-management.html"),
                "text/html; charset=utf-8");
        }).RequireAuthorization();

        app.MapGet("/workday-management",(ClaimsPrincipal user,IWebHostEnvironment environment)=>
        {
            if(!CanViewLocation(user)) return Results.Forbid();

            return Results.File(
                Path.Combine(environment.WebRootPath,"workday-management.html"),
                "text/html; charset=utf-8");
        }).RequireAuthorization();
    }

    private static bool AssignmentAllowed(
        ClaimsPrincipal user,
        string userName,
        Guid assignmentId,
        StudioDatabase db,
        ScheduleStore schedules,
        JobAccessService access,
        string permission)
    {
        PhotographerProfile? profile=new PhotographerStore(db).Get(userName);
        if(profile is null || !profile.IsActive) return false;

        ScheduleEntry? assignment=schedules.GetEntries(entryId:assignmentId).SingleOrDefault();
        if(assignment is null || !assignment.ResourceIds.Contains(profile.ScheduleResourceId)) return false;

        Guid? jobId=ResolveAssignmentJobId(assignment,db);
        if(jobId is not Guid effectiveJobId) return false;

        // Assignment is the grant boundary: ensure a globally defined photographer receives
        // job-scoped staff access even when they are not a standing member of the workspace.

        return access.HasPermission(user,effectiveJobId,permission);
    }

    private static WorkDayAssignment? EnsureAndMapAssignmentAccess(
        ClaimsPrincipal user,
        PhotographerProfile profile,
        ScheduleEntry entry,
        StudioDatabase db,
        WorkDayStore store,
        JobAccessService access)
    {
        Guid? jobId=ResolveAssignmentJobId(entry,db);
        if(jobId is not Guid effectiveJobId) return null;

        if(!access.HasPermission(user,effectiveJobId,"ViewJob")) return null;
        return MapAssignment(entry,db,store,profile.UserName);
    }

    private static Guid? ResolveAssignmentJobId(ScheduleEntry entry,StudioDatabase db)
    {
        if(entry.JobId is Guid jobId) return jobId;
        if(entry.BookingId is not Guid bookingId) return null;
        using Npgsql.NpgsqlConnection c=db.OpenConnection();
        using Npgsql.NpgsqlCommand q=c.CreateCommand();
        q.CommandText="select job_id from studio_bookings where id=$1";
        q.Parameters.AddWithValue(bookingId);
        return q.ExecuteScalar() is Guid value?value:null;
    }

    private static string RequiredJobPermission(WorkDayOperationKind kind)=>kind switch
    {
        WorkDayOperationKind.AcceptAssignment=>"ViewJob",
        WorkDayOperationKind.DeclineAssignment=>"ViewJob",
        WorkDayOperationKind.Arrive=>"ViewJob",
        WorkDayOperationKind.StartSession=>"ViewJob",
        WorkDayOperationKind.CompleteSession=>"ViewJob",
        WorkDayOperationKind.AddNote=>"ViewJob",
        WorkDayOperationKind.RecordException=>"ViewJob",
        WorkDayOperationKind.SetExceptionStatus=>"ViewJob",
        WorkDayOperationKind.LocationObservation=>"ViewJob",
        _=>"ViewJob"
    };

    private static WorkDayAssignment MapAssignment(ScheduleEntry e,StudioDatabase db,WorkDayStore store,string userName)
    {
        string? customer=null,email=null,phone=null;
        Guid? workspace=null,conversation=null;
        if(e.BookingId is Guid bookingId)
        {
            using Npgsql.NpgsqlConnection c=db.OpenConnection();
            using Npgsql.NpgsqlCommand q=c.CreateCommand();
            q.CommandText="select client_name,client_email,client_phone,workspace_id,job_id from studio_bookings where id=$1";
            q.Parameters.AddWithValue(bookingId);
            using Npgsql.NpgsqlDataReader r=q.ExecuteReader();
            if(r.Read())
            {
                customer=r.GetString(0);
                email=r.GetString(1);
                phone=r.IsDBNull(2)?null:r.GetString(2);
                workspace=r.GetGuid(3);
                Guid jobId=r.GetGuid(4);
                r.Close();
                using Npgsql.NpgsqlCommand jq=c.CreateCommand();
                jq.CommandText="select conversation_id from studio_jobs where id=$1";
                jq.Parameters.AddWithValue(jobId);
                if(jq.ExecuteScalar() is Guid cid) conversation=cid;
            }
        }
        else if(e.JobId is Guid jobId)
        {
            using Npgsql.NpgsqlConnection c=db.OpenConnection();
            using Npgsql.NpgsqlCommand q=c.CreateCommand();
            q.CommandText="select workspace_id,conversation_id,client_name from studio_jobs where id=$1";
            q.Parameters.AddWithValue(jobId);
            using Npgsql.NpgsqlDataReader r=q.ExecuteReader();
            if(r.Read()){workspace=r.GetGuid(0);conversation=r.GetGuid(1);customer=r.GetString(2);}
        }

        Guid? effectiveJobId=ResolveAssignmentJobId(e,db);
        var state=store.GetAssignmentState(e.Id,userName);
        return new(
            e.Id,effectiveJobId,e.BookingId,e.Title,e.StartsAtUtc,e.EndsAtUtc,e.Location,
            state?.Status??WorkDaySemantics.InitialStatus(e.Status),e.Notes,workspace,conversation,customer,email,phone,state?.Version??0);
    }

    private static WorkDayLocationTrackingMode ParseTrackingMode(string? value)=>
        Enum.TryParse<WorkDayLocationTrackingMode>(value,true,out var mode)?mode:WorkDayLocationTrackingMode.SignInOutOnly;

    private static bool CanViewLocation(ClaimsPrincipal user)=>
        user.IsInRole("Administrator") ||
        user.HasClaim("permission","Studio.WorkDay.Location.View") ||
        user.HasClaim("capability","Studio.WorkDay.Location.View");

    private static bool CanCorrectWorkSession(ClaimsPrincipal user)=>
        user.IsInRole("Administrator") ||
        user.HasClaim("permission","Studio.WorkDay.Attendance.Correct") ||
        user.HasClaim("capability","Studio.WorkDay.Attendance.Correct");

    private static string UserName(ClaimsPrincipal user)=>
        user.FindFirstValue("login_name")??user.FindFirstValue(ClaimTypes.NameIdentifier)??user.Identity?.Name??string.Empty;
}
