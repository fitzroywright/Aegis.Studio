using Common.Storage;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Claims;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Email;
using Aegis.Studio.Application.EmailRouting;
using Aegis.Studio.Web.Scheduling;
using Aegis.Studio.Web.Production;
using Aegis.Studio.Web.Messaging;
using Aegis.Studio.Web.Notifications;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Storage;
using Aegis.Studio.Web.WorkDay;
using Npgsql;

namespace Aegis.Studio.Web.WorkDay;

/// <summary>
/// Studio-initiated synchronization with the public API Site. This process makes outbound
/// requests only; the API Site never calls back into Studio.
/// </summary>
public sealed class WorkDayApiExchangeWorker(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    ILogger<WorkDayApiExchangeWorker> logger) : BackgroundService
{
    private static readonly Uri ApiBase = new("https://api.bryanstudiosltd.com/api/v1/");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? key=configuration["WorkDay:ApiSyncKey"];
        if(string.IsNullOrWhiteSpace(key))
        {
            logger.LogInformation("WorkDay API exchange is disabled until WorkDay:ApiSyncKey is configured.");
            return;
        }

        using HttpClient http=new(){BaseAddress=ApiBase,Timeout=TimeSpan.FromSeconds(30)};
        http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",key);
        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProvisionAccountsAsync(http,stoppingToken);
                await PublishSnapshotsAsync(http,stoppingToken);
                await PullOperationsAsync(http,stoppingToken);
                await PullAvailabilityAsync(http,stoppingToken);
                await PullUploadsAsync(http,stoppingToken);
                await PullThreadCreatesAsync(http,stoppingToken);
                await PullThreadMessagesAsync(http,stoppingToken);
                await PullConversationMessagesAsync(http,stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(10),stoppingToken);
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception ex)
            {
                logger.LogError(ex,"WorkDay API exchange failed; Studio will retry.");
                try{await Task.Delay(TimeSpan.FromSeconds(20),stoppingToken);}
                catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
            }
        }
    }

    private async Task ProvisionAccountsAsync(HttpClient http,CancellationToken ct)
    {
        using IServiceScope scope=scopes.CreateScope();
        StudioDatabase database=scope.ServiceProvider.GetRequiredService<StudioDatabase>();
        foreach(PhotographerProfile profile in new PhotographerStore(database).GetAll().Where(x=>x.IsActive))
        {
            ct.ThrowIfCancellationRequested();
            using var response=await http.PostAsJsonAsync("admin/workday/users",
                new{userName=profile.UserName,displayName=profile.DisplayName},JsonOptions,ct);
            if(!response.IsSuccessStatusCode)
            {
                logger.LogWarning("WorkDay account provisioning for {UserName} returned HTTP {StatusCode}.",profile.UserName,(int)response.StatusCode);
                continue;
            }
            WorkDayAccountProvision? result=await response.Content.ReadFromJsonAsync<WorkDayAccountProvision>(JsonOptions,ct);
            string? token=result?.EnrollmentToken;
            if(string.IsNullOrWhiteSpace(token))continue;

            string path=configuration["WorkDay:EnrollmentFile"]??"/var/lib/aegis/studio/workday-enrollment-codes.json";
            string directory=Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            List<WorkDayEnrollmentRecord> records=[];
            if(File.Exists(path))
            {
                try{records=JsonSerializer.Deserialize<List<WorkDayEnrollmentRecord>>(await File.ReadAllTextAsync(path,ct))??[];}
                catch(JsonException){logger.LogError("WorkDay enrollment file {Path} is invalid; refusing to overwrite it.",path);continue;}
            }
            WorkDayEnrollmentRecord replacement=new(profile.UserName,profile.DisplayName,token,result?.ExpiresAt);
            int existingIndex=records.FindIndex(x=>x.UserName.Equals(profile.UserName,StringComparison.OrdinalIgnoreCase));
            if(existingIndex>=0)records[existingIndex]=replacement;
            else records.Add(replacement);
            await File.WriteAllTextAsync(path,JsonSerializer.Serialize(records,new JsonSerializerOptions{WriteIndented=true}),ct);
            if(!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite);
            logger.LogInformation("Created one-time WorkDay enrollment for {UserName}; token stored in protected file {Path}.",profile.UserName,path);
        }
    }

    private async Task PublishSnapshotsAsync(HttpClient http,CancellationToken ct)
    {
        using IServiceScope scope=scopes.CreateScope();
        IServiceProvider services=scope.ServiceProvider;
        StudioDatabase database=services.GetRequiredService<StudioDatabase>();
        ScheduleStore schedules=services.GetRequiredService<ScheduleStore>();
        WorkDayStore store=services.GetRequiredService<WorkDayStore>();
        IConfiguration config=services.GetRequiredService<IConfiguration>();
        JobAccessService access=services.GetRequiredService<JobAccessService>();
        WorkspaceAccessService workspaceAccess=services.GetRequiredService<WorkspaceAccessService>();
        WorkspaceStore workspaceStore=services.GetRequiredService<WorkspaceStore>();
        StudioStore studioStore=services.GetRequiredService<StudioStore>();
        StudioInAppMessageStore messages=services.GetRequiredService<StudioInAppMessageStore>();
        NotificationService notifications=services.GetRequiredService<NotificationService>();
        PhotographerStore photographers=new(database);
        System.Text.Json.Nodes.JsonObject[] PhotoContexts(IEnumerable<WorkDayAssignment> assignments)=>assignments.Select(assignment=>
        {
            var context=JsonSerializer.SerializeToNode(assignment,JsonOptions)!.AsObject();
            // Share import's authoritative session mapping; no upload receipt exists while publishing a snapshot.
            context["sessionId"]=assignment.JobId is Guid job?WorkDayPhotoImport.SessionForAssignment(database,job,assignment.Id,Guid.Empty):null;
            return context;
        }).ToArray();
        DateTimeOffset now=DateTimeOffset.UtcNow;
        WorkDayTimePolicy policy=WorkDayTimeSemantics.FromConfiguration(config);
        foreach(PhotographerProfile profile in photographers.GetAll().Where(x=>x.IsActive))
        {
            ct.ThrowIfCancellationRequested();
            ClaimsPrincipal principal=new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier,profile.UserName),
                new Claim("login_name",profile.UserName),
                new Claim(ClaimTypes.Name,profile.DisplayName)
            ],"WorkDayExchange"));
            WorkDayBootstrap? bootstrap=WorkDayEndpoints.BuildBootstrap(principal,database,schedules,store,config,access);
            if(bootstrap is null)continue;
            var threads=messages.GetThreads(principal);
            var threadMessages=threads.SelectMany(thread=>messages.GetMessages(thread.Id,principal,200)).ToArray();
            Dictionary<string,object> sendingIdentities=[];
            foreach(WorkDayAssignment assignment in bootstrap.Today.Concat(bootstrap.Upcoming))
            {
                if(assignment.ConversationId is not Guid conversationId)continue;
                StudioConversation? conversation=studioStore.GetConversation(conversationId);
                if(conversation is null)continue;
                bool canReply=workspaceAccess.HasPermission(principal,conversation.WorkspaceId,"ReplyRequester") ||
                    (conversation.JobId is Guid jobId&&access.HasPermission(principal,jobId,"ReplyRequester"));
                if(!canReply)continue;
                StudioWorkspaceDefinition? workspace=workspaceStore.Get(conversation.WorkspaceId);
                if(string.IsNullOrWhiteSpace(workspace?.EmailAddress))continue;
                sendingIdentities[conversationId.ToString("D")]=new
                {
                    defaultIdentity="Workspace",
                    canSendAsSelf=false,
                    identities=new[]{new{type="Workspace",label=$"Workspace — {workspace.EmailAddress}",fromAddress=workspace.EmailAddress,isDefault=true}}
                };
            }
            var snapshot=new
            {
                bootstrap.UserName,bootstrap.DisplayName,bootstrap.ServerTimeUtc,
                today=PhotoContexts(bootstrap.Today),upcoming=PhotoContexts(bootstrap.Upcoming),
                sendingIdentities,
                bootstrap.ActiveWorkSession,bootstrap.LocationTrackingEnabled,bootstrap.LocationTrackingMode,
                bootstrap.GeneratedAtUtc,bootstrap.FieldTargets,
                availability=photographers.GetAvailability(profile.UserName,now.AddDays(-1),now.AddDays(90)),
                galleryStatus=BuildGalleryStatus(bootstrap,principal,database,schedules,access),
                timeSummary=store.GetTimeSummaries([profile.UserName],now.AddDays(-14),now,policy).Single(),
                threads,messages=threadMessages,notifications=notifications.GetForUser(profile.UserName,100)
            };
            try
            {
                using var published=await http.PutAsJsonAsync("admin/workday/snapshots",
                    new{userName=profile.UserName,revision=now.ToUnixTimeMilliseconds(),snapshot},JsonOptions,ct);
                if(!published.IsSuccessStatusCode)
                    logger.LogWarning("WorkDay snapshot publication for {UserName} returned HTTP {StatusCode}.",profile.UserName,(int)published.StatusCode);
            }
            catch(HttpRequestException ex)
            {
                logger.LogWarning(ex,"WorkDay snapshot publication failed for {UserName}; Studio will retry.",profile.UserName);
            }
        }
    }

    private static Dictionary<string,object> BuildGalleryStatus(
        WorkDayBootstrap bootstrap,
        ClaimsPrincipal principal,
        StudioDatabase database,
        ScheduleStore schedules,
        JobAccessService access)
    {
        Dictionary<string,object> result=[];
        foreach(WorkDayAssignment assignment in bootstrap.Today.Concat(bootstrap.Upcoming))
        {
            if(!WorkDayEndpoints.AssignmentAllowed(principal,bootstrap.UserName,assignment.Id,database,schedules,access,"ViewAttachments"))
                continue;
            ScheduleEntry? entry=schedules.GetEntries(entryId:assignment.Id).SingleOrDefault();
            Guid? jobId=entry is null?null:WorkDayEndpoints.ResolveAssignmentJobId(entry,database);
            if(jobId is not Guid effectiveJobId)continue;
            using NpgsqlConnection connection=database.OpenConnection();
            using NpgsqlCommand workspace=connection.CreateCommand();
            workspace.CommandText="select workspace_id from studio_jobs where id=$1";
            workspace.Parameters.AddWithValue(effectiveJobId);
            if(workspace.ExecuteScalar() is not Guid workspaceId)continue;
            using NpgsqlCommand gallery=connection.CreateCommand();
            gallery.CommandText="""
                select g.id,g.title,g.is_published,g.published_at_utc,count(i.id)
                from studio_workspace_galleries g
                left join studio_gallery_items i on i.gallery_id=g.id and i.is_visible
                where g.workspace_id=$1
                group by g.id,g.title,g.is_published,g.published_at_utc
                """;
            gallery.Parameters.AddWithValue(workspaceId);
            using NpgsqlDataReader reader=gallery.ExecuteReader();
            if(!reader.Read())
                result[assignment.Id.ToString("D")]=new{workspaceId,exists=false,galleryId=(Guid?)null,title=(string?)null,isPublished=false,publishedAtUtc=(DateTimeOffset?)null,visibleItems=0L};
            else
                result[assignment.Id.ToString("D")]=new
                {
                    workspaceId,exists=true,galleryId=reader.GetGuid(0),title=reader.GetString(1),isPublished=reader.GetBoolean(2),
                    publishedAtUtc=reader.IsDBNull(3)?(DateTimeOffset?)null:reader.GetFieldValue<DateTimeOffset>(3),
                    visibleItems=reader.GetInt64(4)
                };
        }
        return result;
    }

    private async Task PullOperationsAsync(HttpClient http,CancellationToken ct)
    {
        using var response=await http.GetAsync("admin/workday/updates?after=0&limit=100&kind=Operation",ct);
        response.EnsureSuccessStatusCode();
        var batch=await response.Content.ReadFromJsonAsync<WorkDayUpdateBatch>(JsonOptions,ct)
            ??throw new InvalidOperationException("The API Site returned an empty WorkDay update batch.");

        foreach(WorkDayUpdate update in batch.Items)
        {
            ct.ThrowIfCancellationRequested();
            WorkDayOperationResult result;
            try
            {
                WorkDayOperationRequest request=update.Payload.Deserialize<WorkDayOperationRequest>(JsonOptions)
                    ??throw new InvalidOperationException("WorkDay operation payload is empty.");
                using IServiceScope scope=scopes.CreateScope();
                WorkDayStore store=scope.ServiceProvider.GetRequiredService<WorkDayStore>();
                result=store.Apply(update.UserName,request);
            }
            catch(InvalidOperationException ex)
            {
                result=new(update.OperationId,WorkDayOperationState.Conflict,DateTimeOffset.UtcNow,ex.Message,null);
            }
            catch(JsonException ex)
            {
                result=new(update.OperationId,WorkDayOperationState.Conflict,DateTimeOffset.UtcNow,"WorkDay update payload is invalid.",null);
                logger.LogWarning(ex,"Invalid WorkDay update {OperationId} received from API Site.",update.OperationId);
            }

            using var ack=await http.PostAsJsonAsync(
                $"admin/workday/updates/{update.OperationId:D}/result",
                new { state=result.State.ToString(), message=result.Message, serverVersion=result.ServerVersion, acknowledgedAtUtc=result.AcknowledgedAtUtc },
                ct);
            ack.EnsureSuccessStatusCode();
        }
    }

    private async Task PullAvailabilityAsync(HttpClient http,CancellationToken ct)
    {
        using var response=await http.GetAsync("admin/workday/updates?after=0&limit=100&kind=AvailabilityUpsert",ct);
        response.EnsureSuccessStatusCode();
        var batch=await response.Content.ReadFromJsonAsync<WorkDayUpdateBatch>(JsonOptions,ct)
            ??throw new InvalidOperationException("The API Site returned an empty WorkDay availability batch.");
        foreach(WorkDayUpdate update in batch.Items)
        {
            ct.ThrowIfCancellationRequested();
            string state="Acknowledged",message="Availability synchronized to Studio.";
            object? availability=null;
            try
            {
                PhotographerAvailabilityRequest request=update.Payload.Deserialize<PhotographerAvailabilityRequest>(JsonOptions)
                    ??throw new InvalidOperationException("Availability payload is empty.");
                using IServiceScope scope=scopes.CreateScope();
                StudioDatabase database=scope.ServiceProvider.GetRequiredService<StudioDatabase>();
                availability=new PhotographerStore(database).AddAvailability(update.UserName,request);
            }
            catch(InvalidOperationException ex){state="Conflict";message=ex.Message;}
            catch(KeyNotFoundException ex){state="Conflict";message=ex.Message;}
            catch(JsonException){state="Conflict";message="WorkDay availability payload is invalid.";}
            using var ack=await http.PostAsJsonAsync(
                $"admin/workday/updates/{update.OperationId:D}/result",
                new{state,message,result=availability,acknowledgedAtUtc=DateTimeOffset.UtcNow},ct);
            ack.EnsureSuccessStatusCode();
        }
    }

    internal async Task PullUploadsAsync(HttpClient http,CancellationToken ct)
    {
        using var response=await http.GetAsync("admin/workday/updates?after=0&limit=20&kind=AssignmentUpload",ct);
        response.EnsureSuccessStatusCode();
        var batch=await response.Content.ReadFromJsonAsync<WorkDayUpdateBatch>(JsonOptions,ct)
            ??throw new InvalidOperationException("The API Site returned an empty WorkDay upload batch.");
        foreach(WorkDayUpdate update in batch.Items)
        {
            ct.ThrowIfCancellationRequested();
            string state="Acknowledged",message="Upload synchronized to Studio.";
            object? uploadResult=null;
            try
            {
                Guid assignmentId=update.Payload.GetProperty("assignmentId").GetGuid();
                Guid uploadId=update.Payload.GetProperty("uploadId").GetGuid();
                string fileName=update.Payload.GetProperty("fileName").GetString()??"upload";
                string contentType=update.Payload.GetProperty("contentType").GetString()??"application/octet-stream";
                bool streamed=update.Payload.TryGetProperty("largeUpload",out var large)&&large.ValueKind==JsonValueKind.True;
                ClaimsPrincipal principal=CreateFieldPrincipal(update.UserName);
                using IServiceScope scope=scopes.CreateScope();
                IServiceProvider services=scope.ServiceProvider;
                StudioDatabase database=services.GetRequiredService<StudioDatabase>();
                ScheduleStore schedules=services.GetRequiredService<ScheduleStore>();
                JobAccessService access=services.GetRequiredService<JobAccessService>();
                StudioStore studio=services.GetRequiredService<StudioStore>();
                AttachmentStore attachments=services.GetRequiredService<AttachmentStore>();
                StudioOperationsFlowPublisher flows=services.GetRequiredService<StudioOperationsFlowPublisher>();
                if(!WorkDayEndpoints.AssignmentAllowed(principal,update.UserName,assignmentId,database,schedules,access,"AddAttachments"))
                    throw new InvalidOperationException("Photographer is not authorized to upload to this assignment.");
                ScheduleEntry? entry=schedules.GetEntries(entryId:assignmentId).SingleOrDefault();
                Guid? jobId=entry is null?null:WorkDayEndpoints.ResolveAssignmentJobId(entry,database);
                if(jobId is not Guid id||studio.GetJob(id) is not { } job)
                    throw new InvalidOperationException("Assignment no longer belongs to an active Studio Job.");
                await using Stream content=streamed
                    ?await WorkDayOriginalDownload.FetchAsync(http,uploadId,update.Payload.GetProperty("sizeBytes").GetInt64(),update.Payload.GetProperty("sha256").GetString()!,ct)
                    :new MemoryStream(Convert.FromBase64String(update.Payload.GetProperty("contentBase64").GetString()??""));
                if(!streamed&&content.Length is 0 or >25_000_000)throw new InvalidOperationException("WorkDay upload size is invalid.");
                FormFile file=new(content,0,content.Length,"file",Path.GetFileName(fileName))
                {Headers=new HeaderDictionary(),ContentType=contentType};
                string correlation=uploadId.ToString("D");
                flows.TryEnqueueWorkDayUpload(correlation,"Upload",assignmentId.ToString("D"));
                WorkDayUploadResult imported=await WorkDayPhotoImport.ImportAsync(database,services.GetRequiredService<IFileStorage>(),attachments,studio,job,assignmentId,uploadId,file,principal,ct,streamed?update.Payload.GetProperty("sha256").GetString():null,update.Payload.TryGetProperty("subjectId",out var subject)&&subject.ValueKind==JsonValueKind.String?subject.GetGuid():null);
                flows.TryEnqueueWorkDayUpload(correlation,"Storage",imported.AttachmentId.ToString("D"));
                flows.TryEnqueueWorkDayUpload(correlation,"MetadataAssociation",imported.AssetId?.ToString("D"));
                flows.TryEnqueueWorkDayUpload(correlation,"Acknowledged",imported.AttachmentId.ToString("D"));
                uploadResult=imported;
            }
            catch(Exception ex) when(ex is InvalidOperationException or ArgumentException or UnauthorizedAccessException){state="Conflict";message=ex is UnauthorizedAccessException?"The upload identity is not authorized.":ex.Message;}
            catch(FormatException){state="Conflict";message="WorkDay upload payload is invalid.";}
            catch(Exception error) when(error is IOException or HttpRequestException)
            {logger.LogWarning("WorkDay original transfer {UploadId} could not be verified; no acknowledgment sent; retrying later.",update.OperationId);continue;}
            using var ack=await http.PostAsJsonAsync(
                $"admin/workday/updates/{update.OperationId:D}/result",
                new{state,message,result=uploadResult,acknowledgedAtUtc=DateTimeOffset.UtcNow},ct);
            ack.EnsureSuccessStatusCode();
        }
    }

    private static ClaimsPrincipal CreateFieldPrincipal(string userName)=>new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier,userName),
        new Claim("login_name",userName),
        new Claim(ClaimTypes.Name,userName)
    ],"WorkDayExchange"));

    private async Task PullThreadCreatesAsync(HttpClient http,CancellationToken ct)
    {
        using var response=await http.GetAsync("admin/workday/updates?after=0&limit=100&kind=ThreadCreate",ct);
        response.EnsureSuccessStatusCode();
        var batch=await response.Content.ReadFromJsonAsync<WorkDayUpdateBatch>(JsonOptions,ct)
            ??throw new InvalidOperationException("The API Site returned an empty WorkDay thread batch.");
        foreach(WorkDayUpdate update in batch.Items)
        {
            ct.ThrowIfCancellationRequested();
            string state="Acknowledged",message="Message thread synchronized to Studio.";
            object? thread=null;
            try
            {
                string scopeType=update.Payload.GetProperty("scopeType").GetString()??"";
                Guid scopeId=update.Payload.GetProperty("scopeId").GetGuid();
                ClaimsPrincipal principal=new(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier,update.UserName),
                    new Claim("login_name",update.UserName),
                    new Claim(ClaimTypes.Name,update.UserName)
                ],"WorkDayExchange"));
                using IServiceScope scope=scopes.CreateScope();
                StudioInAppMessageStore store=scope.ServiceProvider.GetRequiredService<StudioInAppMessageStore>();
                thread=store.GetOrCreateScoped(principal,scopeType,scopeId);
            }
            catch(InvalidOperationException ex){state="Conflict";message=ex.Message;}
            catch(JsonException){state="Conflict";message="WorkDay thread request is invalid.";}
            using var ack=await http.PostAsJsonAsync(
                $"admin/workday/updates/{update.OperationId:D}/result",
                new{state,message,result=thread,acknowledgedAtUtc=DateTimeOffset.UtcNow},ct);
            ack.EnsureSuccessStatusCode();
        }
    }

    private async Task PullThreadMessagesAsync(HttpClient http,CancellationToken ct)
    {
        using var response=await http.GetAsync("admin/workday/updates?after=0&limit=100&kind=ThreadMessage",ct);
        response.EnsureSuccessStatusCode();
        var batch=await response.Content.ReadFromJsonAsync<WorkDayUpdateBatch>(JsonOptions,ct)
            ??throw new InvalidOperationException("The API Site returned an empty WorkDay message batch.");
        foreach(WorkDayUpdate update in batch.Items)
        {
            ct.ThrowIfCancellationRequested();
            string state="Acknowledged",message="Message synchronized to Studio.";
            try
            {
                Guid threadId=update.Payload.GetProperty("threadId").GetGuid();
                string body=update.Payload.GetProperty("body").GetString()??"";
                ClaimsPrincipal principal=new(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier,update.UserName),
                    new Claim("login_name",update.UserName),
                    new Claim(ClaimTypes.Name,update.UserName)
                ],"WorkDayExchange"));
                using IServiceScope scope=scopes.CreateScope();
                StudioInAppMessageStore store=scope.ServiceProvider.GetRequiredService<StudioInAppMessageStore>();
                store.AddMessage(threadId,principal,body,update.OperationId);
            }
            catch(InvalidOperationException ex){state="Conflict";message=ex.Message;}
            catch(JsonException){state="Conflict";message="WorkDay message payload is invalid.";}
            using var ack=await http.PostAsJsonAsync(
                $"admin/workday/updates/{update.OperationId:D}/result",
                new{state,message,acknowledgedAtUtc=DateTimeOffset.UtcNow},ct);
            ack.EnsureSuccessStatusCode();
        }
    }

    private async Task PullConversationMessagesAsync(HttpClient http,CancellationToken ct)
    {
        using var response=await http.GetAsync("admin/workday/updates?after=0&limit=100&kind=ConversationMessage",ct);
        response.EnsureSuccessStatusCode();
        var batch=await response.Content.ReadFromJsonAsync<WorkDayUpdateBatch>(JsonOptions,ct)
            ??throw new InvalidOperationException("The API Site returned an empty WorkDay conversation message batch.");
        foreach(WorkDayUpdate update in batch.Items)
        {
            ct.ThrowIfCancellationRequested();
            string state="Acknowledged",message="Conversation reply synchronized to Studio.";
            try
            {
                Guid conversationId=update.Payload.GetProperty("conversationId").GetGuid();
                string body=update.Payload.GetProperty("body").GetString()??"";
                bool isInternal=update.Payload.TryGetProperty("isInternal",out JsonElement internalValue)&&internalValue.GetBoolean();
                string? requestedIdentity=update.Payload.TryGetProperty("sendingIdentity",out JsonElement identityValue)?identityValue.GetString():null;
                if(string.IsNullOrWhiteSpace(body)||body.Length>16000)throw new InvalidOperationException("Conversation message must contain 1 to 16000 characters.");
                using IServiceScope scope=scopes.CreateScope();
                IServiceProvider services=scope.ServiceProvider;
                StudioStore store=services.GetRequiredService<StudioStore>();
                WorkspaceAccessService workspaceAccess=services.GetRequiredService<WorkspaceAccessService>();
                JobAccessService jobAccess=services.GetRequiredService<JobAccessService>();
                WorkspaceStore workspaces=services.GetRequiredService<WorkspaceStore>();
                StudioConversation conversation=store.GetConversation(conversationId)
                    ??throw new InvalidOperationException("Conversation was not found.");
                ClaimsPrincipal principal=CreateFieldPrincipal(update.UserName);
                bool allowed=workspaceAccess.HasPermission(principal,conversation.WorkspaceId,"ReplyRequester")||
                    (conversation.JobId is Guid jobId&&jobAccess.HasPermission(principal,jobId,"ReplyRequester"));
                if(!allowed)throw new InvalidOperationException("Photographer is not authorized to reply to this conversation.");
                bool externalEmailReply=!isInternal&&conversation.Channel.Equals("Email",StringComparison.OrdinalIgnoreCase);
                StudioSendingIdentity? identity=null;
                if(externalEmailReply)
                {
                    StudioWorkspaceDefinition workspace=workspaces.Get(conversation.WorkspaceId)
                        ??throw new InvalidOperationException("The conversation workspace no longer exists.");
                    string[] allowedDomains=[];
                    identity=StudioSendingIdentityResolver.Resolve(requestedIdentity,workspace.EmailAddress??string.Empty,update.UserName,string.Empty,false,allowedDomains);
                }
                StudioConversation updated=store.AddMessage(conversationId,new AddMessageRequest(body,isInternal,requestedIdentity),principal,update.OperationId);
                if(externalEmailReply)
                    services.GetRequiredService<OutboundEmailStore>().QueueIdempotent(updated,body,identity,update.OperationId);
            }
            catch(InvalidOperationException ex){state="Conflict";message=ex.Message;}
            catch(UnauthorizedAccessException ex){state="Conflict";message=ex.Message;}
            catch(JsonException){state="Conflict";message="WorkDay conversation message payload is invalid.";}
            using var ack=await http.PostAsJsonAsync(
                $"admin/workday/updates/{update.OperationId:D}/result",
                new{state,message,acknowledgedAtUtc=DateTimeOffset.UtcNow},ct);
            ack.EnsureSuccessStatusCode();
        }
    }

    private sealed record WorkDayAccountProvision(string UserName,string DisplayName,bool EnrollmentRequired,string? EnrollmentToken,string? ExpiresAt);
    private sealed record WorkDayEnrollmentRecord(string UserName,string DisplayName,string Token,string? ExpiresAt);
    private sealed record WorkDayUpdateBatch(IReadOnlyList<WorkDayUpdate> Items);
    private sealed record WorkDayUpdate(long Sequence,Guid OperationId,string UserName,string Kind,JsonElement Payload,DateTimeOffset CreatedAt);
}
