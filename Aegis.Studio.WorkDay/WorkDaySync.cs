using Aegis.Studio.WorkDay.Core;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Aegis.Studio.WorkDay;

public sealed class WorkDayApiClient
{
    public static readonly Uri ApiBaseUri=new("https://api.bryanstudiosltd.com/api/v1/");
    private readonly HttpClient http=new(){Timeout=TimeSpan.FromSeconds(15)};
    public Uri? Server=>http.BaseAddress;
    public void SetServer(Uri server)
    {
        if(server.Scheme!="https"||!server.Host.Equals("api.bryanstudiosltd.com",StringComparison.OrdinalIgnoreCase)||server.AbsolutePath!="/api/v1/")
            throw new ArgumentException("WorkDay can connect only through the Bryan's API Site.",nameof(server));
        http.BaseAddress=ApiBaseUri;
    }
    public void SetDeviceToken(string? token)
    {
        http.DefaultRequestHeaders.Remove("X-Aegis-WorkDay-Token");
        if(!string.IsNullOrWhiteSpace(token)) http.DefaultRequestHeaders.TryAddWithoutValidation("X-Aegis-WorkDay-Token",token);
    }

    public async Task LoginAsync(string user,string password,CancellationToken ct=default)
    {
        using HttpResponseMessage r=await http.PostAsJsonAsync("auth/login",new{userName=user,password},ct);
        if(r.StatusCode==System.Net.HttpStatusCode.Unauthorized) throw new InvalidOperationException("Authentication failed.");
        r.EnsureSuccessStatusCode();
    }

    public Task<WorkDayBootstrapDto?> BootstrapAsync(CancellationToken ct=default)=>
        http.GetFromJsonAsync<WorkDayBootstrapDto>("workday/bootstrap",ct);

    public async Task<DeviceSessionDto> IssueDeviceSessionAsync(string deviceId,CancellationToken ct=default)
    {
        using HttpResponseMessage r=await http.PostAsJsonAsync("workday/device-session",new{deviceId},ct);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<DeviceSessionDto>(cancellationToken:ct) ?? throw new InvalidOperationException("Studio did not issue a device session.");
    }

    public async Task RevokeDeviceSessionAsync(string deviceId,CancellationToken ct=default)
    {
        using HttpResponseMessage r=await http.PostAsJsonAsync("workday/device-session/revoke",new{deviceId},ct);
        r.EnsureSuccessStatusCode();
    }

    public async Task PublishObservationAsync(WorkDayClientObservationDto observation,CancellationToken ct=default)
    {
        using HttpResponseMessage r=await http.PostAsJsonAsync("workday/client-observation",observation,ct);
        r.EnsureSuccessStatusCode();
    }

    public async Task<OperationResultDto> SendAsync(LocalOperation op,CancellationToken ct=default)
    {
        object? location=op.Latitude.HasValue&&op.Longitude.HasValue
            ? new{observationId=op.LocationObservationId??op.OperationId,latitude=op.Latitude.Value,longitude=op.Longitude.Value,accuracyMetres=op.AccuracyMetres,observedAtUtc=op.LocationObservedAtUtc??op.OccurredAtUtc,source=op.LocationSource,mockLocation=(bool?)null}
            : null;
        using HttpResponseMessage submitted=await http.PostAsJsonAsync("workday/operations",new{operationId=op.OperationId,kind=op.Kind,assignmentId=op.AssignmentId,occurredAtUtc=op.OccurredAtUtc,deviceId=op.DeviceId,text=op.Text,category=op.Category,location,expectedVersion=op.ExpectedVersion},ct);
        if(submitted.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Conflict)
        {
            string message=await ErrorAsync(submitted,ct);
            return new(op.OperationId,"Conflict",DateTimeOffset.UtcNow,message,null);
        }
        submitted.EnsureSuccessStatusCode();
        if(submitted.StatusCode!=System.Net.HttpStatusCode.Accepted)
            return await submitted.Content.ReadFromJsonAsync<OperationResultDto>(cancellationToken:ct)
                ??throw new InvalidOperationException("The API Site returned no WorkDay operation receipt.");

        DateTimeOffset deadline=DateTimeOffset.UtcNow.AddMinutes(2);
        while(DateTimeOffset.UtcNow<deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(3),ct);
            using HttpResponseMessage status=await http.GetAsync($"workday/operations/{op.OperationId:D}",ct);
            status.EnsureSuccessStatusCode();
            OperationResultDto result=await status.Content.ReadFromJsonAsync<OperationResultDto>(cancellationToken:ct)
                ??throw new InvalidOperationException("The API Site returned no WorkDay operation status.");
            if(result.State is "Acknowledged" or "Conflict")return result;
        }
        throw new HttpRequestException("The API Site has stored this update and is waiting for Studio to process it.");
    }

    public async Task<UploadResultDto> UploadAsync(LocalUpload upload,CancellationToken ct=default)
    {
        using MultipartFormDataContent form=new();
        form.Add(new StringContent(upload.UploadId.ToString("D")),"uploadId");
        await using FileStream fs=File.OpenRead(upload.LocalPath);
        using StreamContent file=new(fs);
        file.Headers.ContentType=new MediaTypeHeaderValue(upload.ContentType);
        form.Add(file,"file",upload.FileName);
        using HttpResponseMessage r=await http.PostAsync($"workday/assignments/{upload.AssignmentId:D}/uploads",form,ct);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<UploadResultDto>(cancellationToken:ct) ?? throw new InvalidOperationException("Studio did not acknowledge the upload.");
    }

    public Task<WorkDayTimeSummaryDto?> GetTimeSummaryAsync(DateTimeOffset? from=null,DateTimeOffset? to=null,CancellationToken ct=default)
    {
        string query="workday/time-summary";
        List<string> parts=[];
        if(from.HasValue) parts.Add("from="+Uri.EscapeDataString(from.Value.ToString("O")));
        if(to.HasValue) parts.Add("to="+Uri.EscapeDataString(to.Value.ToString("O")));
        if(parts.Count>0) query+="?"+string.Join("&",parts);
        return http.GetFromJsonAsync<WorkDayTimeSummaryDto>(query,ct);
    }

    public Task<List<PhotographerAvailabilityDto>?> GetAvailabilityAsync(DateTimeOffset from,DateTimeOffset to,CancellationToken ct=default)=>
        http.GetFromJsonAsync<List<PhotographerAvailabilityDto>>($"workday/availability?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}",ct);

    public async Task<PhotographerAvailabilityDto> AddAvailabilityAsync(DateTimeOffset startsAtUtc,DateTimeOffset endsAtUtc,string availabilityType,string? note,Guid? operationId=null,CancellationToken ct=default)
    {
        using HttpResponseMessage r=await http.PostAsJsonAsync("workday/availability",new{startsAtUtc,endsAtUtc,availabilityType,note,operationId},ct);
        if(!r.IsSuccessStatusCode)throw new HttpRequestException(await ErrorAsync(r,ct),null,r.StatusCode);
        return await r.Content.ReadFromJsonAsync<PhotographerAvailabilityDto>(cancellationToken:ct)??throw new InvalidOperationException("Studio did not return the availability entry.");
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response,CancellationToken ct)
    {
        try
        {
            using var payload=System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if(payload.RootElement.TryGetProperty("error",out var error)&&error.ValueKind==System.Text.Json.JsonValueKind.String)return error.GetString()!;
        }
        catch(System.Text.Json.JsonException) { }
        return $"Studio rejected this report ({(int)response.StatusCode}). Review it with the Job Owner.";
    }

    public Task<List<MessageThreadDto>?> GetThreadsAsync(CancellationToken ct=default)=>http.GetFromJsonAsync<List<MessageThreadDto>>("messages/threads",ct);
    public async Task<MessageThreadDto> GetOrCreateThreadAsync(string scopeType,Guid scopeId,CancellationToken ct=default)
    {
        using HttpResponseMessage r=await http.PostAsync($"messages/threads/{Uri.EscapeDataString(scopeType)}/{scopeId:D}",null,ct);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<MessageThreadDto>(cancellationToken:ct)??throw new InvalidOperationException("Studio did not return the message thread.");
    }
    public Task<List<MessageDto>?> GetMessagesAsync(Guid threadId,CancellationToken ct=default)=>http.GetFromJsonAsync<List<MessageDto>>($"messages/threads/{threadId:D}",ct);
    public async Task SendThreadMessageAsync(Guid threadId,string body,CancellationToken ct=default){using HttpResponseMessage r=await http.PostAsJsonAsync($"messages/threads/{threadId:D}",new{body},ct);r.EnsureSuccessStatusCode();}
    public Task<List<NotificationDto>?> GetNotificationsAsync(CancellationToken ct=default)=>http.GetFromJsonAsync<List<NotificationDto>>("notifications?count=50",ct);
    public Task<AssignmentGalleryStatusDto?> GetAssignmentGalleryStatusAsync(Guid assignmentId,CancellationToken ct=default)=>http.GetFromJsonAsync<AssignmentGalleryStatusDto>($"workday/assignments/{assignmentId:D}/gallery-status",ct);
    public Task<SendingIdentityOptionsDto?> GetSendingIdentitiesAsync(Guid conversationId,CancellationToken ct=default)=>http.GetFromJsonAsync<SendingIdentityOptionsDto>($"conversations/{conversationId:D}/sending-identities",ct);
    public async Task ReplyConversationAsync(Guid conversationId,string body,string identity,CancellationToken ct=default){using HttpResponseMessage r=await http.PostAsJsonAsync($"conversations/{conversationId:D}/messages",new{body,isInternal=false,sendingIdentity=identity},ct);r.EnsureSuccessStatusCode();}
}

public sealed class WorkDaySyncEngine(WorkDayLocalStore store,WorkDayApiClient api)
{
    private readonly SemaphoreSlim gate=new(1,1);
    public DateTimeOffset? LastAttemptUtc{get;private set;}
    public DateTimeOffset? LastSuccessUtc{get;private set;}
    public string State{get;private set;}="Unknown";
    public event Action? Changed;
    public int CompletedUpdates{get;private set;}
    public int TotalUpdates{get;private set;}
    public string? LastError{get;private set;}
    private void Publish()=>Changed?.Invoke();
    public async Task LoadStatusAsync()
    {
        var saved=await store.GetMetadataAsync("LastSuccessfulSyncUtc");
        if(DateTimeOffset.TryParse(saved,out var stamp))LastSuccessUtc=stamp;
    }
    public bool LocationTrackingEnabled{get;private set;}
    public string LocationTrackingMode{get;private set;}="Unknown";

    public async Task SynchronizeAsync(CancellationToken ct=default)
    {
        if(!await gate.WaitAsync(0,ct))return;
        try
        {
            await LoadStatusAsync();
            LastAttemptUtc=DateTimeOffset.UtcNow;State="Sending";LastError=null;CompletedUpdates=0;
            TotalUpdates=(await store.PendingAsync()).Count+(await store.GetAvailabilityAsync()).Count(x=>x.SyncState is "Pending" or "Failed" or "Sending");Publish();
            await RefreshAssignmentsCoreAsync(ct);
            foreach(var availability in (await store.GetAvailabilityAsync()).Where(x=>x.SyncState is "Pending" or "Failed" or "Sending"))
            {
                availability.SyncState="Sending";availability.AttemptCount++;await store.SaveAvailabilityAsync(availability);
                try
                {
                    var result=await api.AddAvailabilityAsync(availability.StartsAtUtc,availability.EndsAtUtc,availability.AvailabilityType,availability.Note,availability.Id,ct);
                    if(result.Id!=availability.Id)throw new InvalidOperationException("Studio did not acknowledge this availability operation. Update Studio before retrying.");
                    availability.SyncState="Acknowledged";availability.LastError=null;
                }
                catch(HttpRequestException ex) when(ex.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Conflict)
                {availability.SyncState="Conflict";availability.LastError=ex.Message;}
                catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
                {availability.SyncState="Failed";availability.LastError=ex.Message;await store.SaveAvailabilityAsync(availability);State="Waiting";LastError=ex.Message;Publish();return;}
                await store.SaveAvailabilityAsync(availability);CompletedUpdates++;Publish();
            }

            HashSet<Guid> blocked=(await store.UnacknowledgedAsync()).Where(x=>x.State==nameof(LocalSyncState.Conflict)&&x.AssignmentId.HasValue).Select(x=>x.AssignmentId!.Value).ToHashSet();
            foreach(LocalOperation op in await store.PendingAsync())
            {
                if(op.AssignmentId is Guid blockedId&&blocked.Contains(blockedId))continue;
                op.State=nameof(LocalSyncState.Sending);op.LastAttemptUtc=DateTimeOffset.UtcNow;op.AttemptCount++;await store.SaveOperationAsync(op);
                try
                {
                    OperationResultDto result=await api.SendAsync(op,ct);
                    op.State=WorkDayClientRules.IsAcknowledged(result.State)?nameof(LocalSyncState.Acknowledged):nameof(LocalSyncState.Conflict);
                    op.LastError=result.Message;
                    if(op.State==nameof(LocalSyncState.Conflict)&&op.AssignmentId is Guid conflictId)blocked.Add(conflictId);
                    if(op.State==nameof(LocalSyncState.Acknowledged)&&op.AssignmentId is Guid id && result.ServerVersion is long version)
                    {
                        LocalAssignment? assignment=await store.GetAssignmentAsync(id);
                        if(assignment is not null&&version>=assignment.Version){assignment.Version=version;assignment.Status=OperationStatus(op.Kind,op.Category,assignment.Status);await store.ReplaceAssignmentsAsync([assignment]);}
                    }
                }
                catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
                {
                    op.State=nameof(LocalSyncState.Failed);op.LastError=ex.Message;await store.SaveOperationAsync(op);State="Waiting";LastError=ex.Message;Publish();return;
                }
                await store.SaveOperationAsync(op);CompletedUpdates++;Publish();
            }
            await RefreshAssignmentsCoreAsync(ct);
            var remaining=await store.UnacknowledgedAsync();
            bool outstanding=remaining.Count>0||(await store.GetAvailabilityAsync()).Any(x=>WorkDayClientRules.IsOutstanding(x.SyncState));
            State=outstanding?"Degraded":"Healthy";
            if(!outstanding){LastSuccessUtc=DateTimeOffset.UtcNow;await store.SetMetadataAsync("LastSuccessfulSyncUtc",LastSuccessUtc.Value.ToString("O"));}
            else LastError="Some changes need review. Open Review reports / resolve conflict; original reports remain on this device.";
            Publish();
        }
        catch(Exception ex)
        {State="Waiting";LastError=ex.Message;Publish();throw;}
        finally{gate.Release();}
    }

    private async Task RefreshAssignmentsCoreAsync(CancellationToken ct)
    {
        WorkDayBootstrapDto bootstrap=await api.BootstrapAsync(ct)??throw new InvalidOperationException("Studio returned no WorkDay bootstrap.");
        LocationTrackingEnabled=bootstrap.LocationTrackingEnabled;LocationTrackingMode=bootstrap.LocationTrackingMode;
        await store.ReplaceAssignmentsAsync(bootstrap.Today.Concat(bootstrap.Upcoming).Select(x=>new LocalAssignment
        {
            Id=x.Id,JobId=x.JobId,BookingId=x.BookingId,WorkspaceId=x.WorkspaceId,ConversationId=x.ConversationId,
            Title=x.Title,StartsAtUtc=x.StartsAtUtc,EndsAtUtc=x.EndsAtUtc,Location=x.Location,Status=x.Status,Instructions=x.Instructions,
            ClientName=x.ClientName,ClientEmail=x.ClientEmail,ClientPhone=x.ClientPhone,Version=x.Version,
            FieldTargetsJson=CachedFieldTargets(bootstrap,x.Id),IsCurrent=true,CachedAtUtc=DateTimeOffset.UtcNow
        }),authoritative:true);
    }

    private static string? CachedFieldTargets(WorkDayBootstrapDto bootstrap,Guid assignment)=>
        bootstrap.FieldTargets is { } map&&map.TryGetValue(assignment,out var targets)?System.Text.Json.JsonSerializer.Serialize(targets):null;

    public async Task ResolveAssignmentConflictAsync(Guid assignmentId,string reason,CancellationToken ct=default)
    {
        await gate.WaitAsync(ct);
        try
        {
            await RefreshAssignmentsCoreAsync(ct);
            await store.ResolveAssignmentConflictAsync(assignmentId,reason);
            State=(await store.UnacknowledgedAsync()).Count>0?"Pending":"Healthy";
        }
        finally{gate.Release();}
    }

    private static string OperationStatus(string kind,string? category,string current)=>WorkDayClientRules.AssignmentStatus(kind,category,current);
}

public sealed record WorkDayBootstrapDto(string UserName,string DisplayName,DateTimeOffset ServerTimeUtc,IReadOnlyList<AssignmentDto> Today,IReadOnlyList<AssignmentDto> Upcoming,WorkSessionDto? ActiveWorkSession,bool LocationTrackingEnabled,string LocationTrackingMode,DateTimeOffset GeneratedAtUtc,IReadOnlyDictionary<Guid,IReadOnlyList<FieldTargetDto>>? FieldTargets=null);
public sealed record AssignmentDto(Guid Id,Guid? JobId,Guid? BookingId,string Title,DateTimeOffset StartsAtUtc,DateTimeOffset EndsAtUtc,string? Location,string Status,string? Instructions,Guid? WorkspaceId,Guid? ConversationId,string? ClientName,string? ClientEmail,string? ClientPhone,long Version);
public sealed record WorkSessionDto(Guid Id,string UserName,DateTimeOffset StartedAtUtc,DateTimeOffset? EndedAtUtc,string DeviceId,bool StartedOffline,bool? EndedOffline,string Source,DateTimeOffset UpdatedAtUtc);
public sealed record OperationResultDto(Guid OperationId,string State,DateTimeOffset AcknowledgedAtUtc,string? Message,long? ServerVersion);
public sealed record MessageThreadDto(Guid Id,string ScopeType,Guid? ScopeId,string Title,string CreatedBy,DateTimeOffset CreatedAtUtc,DateTimeOffset? LastMessageAtUtc,long UnreadCount);
public sealed record MessageDto(Guid Id,Guid ThreadId,string SenderUserName,string Body,DateTimeOffset CreatedAtUtc);
public sealed record NotificationDto(Guid Id,string UserName,string Channel,string EventType,string Subject,string Body,string? ReferenceType,string? ReferenceId,string Status,int Attempts,string? LastError,DateTimeOffset CreatedAtUtc,DateTimeOffset? DeliveredAtUtc,bool IsRead);
public sealed record GalleryDto(Guid Id,Guid WorkspaceId,string Title,string? Description,string PublicToken,bool IsPublished,DateTimeOffset? PublishedAtUtc,DateTimeOffset CreatedAtUtc,DateTimeOffset UpdatedAtUtc,IReadOnlyList<object> Items);
public sealed record SendingIdentityOptionDto(string Type,string Label,string FromAddress,bool IsDefault);
public sealed record SendingIdentityOptionsDto(string DefaultIdentity,bool CanSendAsSelf,IReadOnlyList<SendingIdentityOptionDto> Identities);

public sealed record DeviceSessionDto(Guid SessionId,string Token,DateTimeOffset ExpiresAtUtc);

public sealed record WorkDayClientObservationDto(string DeviceId,DateTimeOffset ObservedAtUtc,string Connectivity,string SynchronizationState,int PendingOperations,DateTimeOffset? OldestPendingAtUtc,int PendingUploads,int FailedUploads,bool WorkSessionActive,string LocationPermissionState);

public sealed record PhotographerAvailabilityDto(Guid Id,string UserName,DateTimeOffset StartsAtUtc,DateTimeOffset EndsAtUtc,string AvailabilityType,string? Note,DateTimeOffset CreatedAtUtc);

public sealed record AssignmentGalleryStatusDto(Guid WorkspaceId,bool Exists,Guid? GalleryId,string? Title,bool IsPublished,DateTimeOffset? PublishedAtUtc,long VisibleItems);

public sealed record WorkDayTimeBreakdownDto(string UserName,DateOnly LocalDate,double TotalMinutes,double RegularMinutes,double OvertimeMinutes,double WeekendMinutes,double WeekendOvertimeMinutes);
public sealed record WorkDayTimeSummaryDto(string UserName,DateTimeOffset FromUtc,DateTimeOffset ToUtc,double TotalMinutes,double RegularMinutes,double OvertimeMinutes,double WeekendMinutes,double WeekendOvertimeMinutes,IReadOnlyList<WorkDayTimeBreakdownDto> Days);

public sealed record FieldTargetDto(string TargetId,string Scope,string Name,string SourceFingerprint,long Version,string? Status,string? Collection=null,string? Reference=null,string? Instructions=null);
public sealed record FieldStatusDto(string TargetId,string Status,string SourceFingerprint,long ExpectedTargetVersion,string? Reason=null);
