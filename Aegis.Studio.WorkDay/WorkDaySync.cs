using Aegis.Studio.WorkDay.Core;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Aegis.Studio.WorkDay;

public sealed class WorkDayApiClient
{
    private readonly HttpClient http=new(){Timeout=TimeSpan.FromSeconds(15)};
    public Uri? Server=>http.BaseAddress;
    public void SetServer(Uri server)=>http.BaseAddress=server;
    public void SetDeviceToken(string? token)
    {
        http.DefaultRequestHeaders.Remove("X-Aegis-WorkDay-Token");
        if(!string.IsNullOrWhiteSpace(token)) http.DefaultRequestHeaders.TryAddWithoutValidation("X-Aegis-WorkDay-Token",token);
    }

    public async Task LoginAsync(string user,string password,CancellationToken ct=default)
    {
        using HttpResponseMessage r=await http.PostAsJsonAsync("api/auth/login",new{userName=user,password},ct);
        if(r.StatusCode==System.Net.HttpStatusCode.Unauthorized) throw new InvalidOperationException("Authentication failed.");
        r.EnsureSuccessStatusCode();
    }

    public Task<WorkDayBootstrapDto?> BootstrapAsync(CancellationToken ct=default)=>
        http.GetFromJsonAsync<WorkDayBootstrapDto>("api/workday/bootstrap",ct);

    public async Task<DeviceSessionDto> IssueDeviceSessionAsync(string deviceId,CancellationToken ct=default)
    {
        using HttpResponseMessage r=await http.PostAsJsonAsync("api/workday/device-session",new{deviceId},ct);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<DeviceSessionDto>(cancellationToken:ct) ?? throw new InvalidOperationException("Studio did not issue a device session.");
    }

    public async Task RevokeDeviceSessionAsync(string deviceId,CancellationToken ct=default)
    {
        using HttpResponseMessage r=await http.PostAsJsonAsync("api/workday/device-session/revoke",new{deviceId},ct);
        r.EnsureSuccessStatusCode();
    }

    public async Task PublishObservationAsync(WorkDayClientObservationDto observation,CancellationToken ct=default)
    {
        using HttpResponseMessage r=await http.PostAsJsonAsync("api/workday/client-observation",observation,ct);
        r.EnsureSuccessStatusCode();
    }

    public async Task<OperationResultDto> SendAsync(LocalOperation op,CancellationToken ct=default)
    {
        object? location=op.Latitude.HasValue&&op.Longitude.HasValue
            ? new{observationId=op.LocationObservationId??op.OperationId,latitude=op.Latitude.Value,longitude=op.Longitude.Value,accuracyMetres=op.AccuracyMetres,observedAtUtc=op.LocationObservedAtUtc??op.OccurredAtUtc,source=op.LocationSource,mockLocation=(bool?)null}
            : null;
        using HttpResponseMessage r=await http.PostAsJsonAsync("api/workday/operations",new{operationId=op.OperationId,kind=op.Kind,assignmentId=op.AssignmentId,occurredAtUtc=op.OccurredAtUtc,deviceId=op.DeviceId,text=op.Text,category=op.Category,location,expectedVersion=op.ExpectedVersion},ct);
        OperationResultDto? result=await r.Content.ReadFromJsonAsync<OperationResultDto>(cancellationToken:ct);
        if(result is null) throw new InvalidOperationException("Studio did not return an operation acknowledgement.");
        return result;
    }

    public async Task<UploadResultDto> UploadAsync(LocalUpload upload,CancellationToken ct=default)
    {
        using MultipartFormDataContent form=new();
        form.Add(new StringContent(upload.UploadId.ToString("D")),"uploadId");
        await using FileStream fs=File.OpenRead(upload.LocalPath);
        using StreamContent file=new(fs);
        file.Headers.ContentType=new MediaTypeHeaderValue(upload.ContentType);
        form.Add(file,"file",upload.FileName);
        using HttpResponseMessage r=await http.PostAsync($"api/workday/assignments/{upload.AssignmentId:D}/uploads",form,ct);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<UploadResultDto>(cancellationToken:ct) ?? throw new InvalidOperationException("Studio did not acknowledge the upload.");
    }

    public Task<List<MessageThreadDto>?> GetThreadsAsync(CancellationToken ct=default)=>http.GetFromJsonAsync<List<MessageThreadDto>>("api/messages/threads",ct);
    public async Task<MessageThreadDto> GetOrCreateThreadAsync(string scopeType,Guid scopeId,CancellationToken ct=default)
    {
        using HttpResponseMessage r=await http.PostAsync($"api/messages/threads/{Uri.EscapeDataString(scopeType)}/{scopeId:D}",null,ct);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<MessageThreadDto>(cancellationToken:ct)??throw new InvalidOperationException("Studio did not return the message thread.");
    }
    public Task<List<MessageDto>?> GetMessagesAsync(Guid threadId,CancellationToken ct=default)=>http.GetFromJsonAsync<List<MessageDto>>($"api/messages/threads/{threadId:D}",ct);
    public async Task SendThreadMessageAsync(Guid threadId,string body,CancellationToken ct=default){using HttpResponseMessage r=await http.PostAsJsonAsync($"api/messages/threads/{threadId:D}",new{body},ct);r.EnsureSuccessStatusCode();}
    public Task<List<NotificationDto>?> GetNotificationsAsync(CancellationToken ct=default)=>http.GetFromJsonAsync<List<NotificationDto>>("api/notifications?count=50",ct);
    public Task<GalleryDto?> GetGalleryAsync(Guid workspaceId,CancellationToken ct=default)=>http.GetFromJsonAsync<GalleryDto>($"api/workspaces/{workspaceId:D}/gallery",ct);
    public Task<SendingIdentityOptionsDto?> GetSendingIdentitiesAsync(Guid conversationId,CancellationToken ct=default)=>http.GetFromJsonAsync<SendingIdentityOptionsDto>($"api/conversations/{conversationId:D}/sending-identities",ct);
    public async Task ReplyConversationAsync(Guid conversationId,string body,string identity,CancellationToken ct=default){using HttpResponseMessage r=await http.PostAsJsonAsync($"api/conversations/{conversationId:D}/messages",new{body,isInternal=false,sendingIdentity=identity},ct);r.EnsureSuccessStatusCode();}
}

public sealed class WorkDaySyncEngine(WorkDayLocalStore store,WorkDayApiClient api,WorkDayUploadService uploads)
{
    private readonly SemaphoreSlim gate=new(1,1);
    public DateTimeOffset? LastAttemptUtc{get;private set;}
    public DateTimeOffset? LastSuccessUtc{get;private set;}
    public string State{get;private set;}="Unknown";
    public string LocationTrackingMode{get;private set;}="Unknown";

    public async Task SynchronizeAsync(CancellationToken ct=default)
    {
        if(!await gate.WaitAsync(0,ct))return;
        try
        {
            LastAttemptUtc=DateTimeOffset.UtcNow;State="Sending";
            WorkDayBootstrapDto? bootstrap=await api.BootstrapAsync(ct);
            if(bootstrap is null) throw new InvalidOperationException("Studio returned no WorkDay bootstrap.");
            LocationTrackingMode=bootstrap.LocationTrackingMode;

            await store.ReplaceAssignmentsAsync(bootstrap.Today.Concat(bootstrap.Upcoming).Select(x=>new LocalAssignment
            {
                Id=x.Id,JobId=x.JobId,BookingId=x.BookingId,WorkspaceId=x.WorkspaceId,ConversationId=x.ConversationId,
                Title=x.Title,StartsAtUtc=x.StartsAtUtc,EndsAtUtc=x.EndsAtUtc,Location=x.Location,Status=x.Status,Instructions=x.Instructions,
                CustomerName=x.CustomerName,CustomerEmail=x.CustomerEmail,CustomerPhone=x.CustomerPhone,Version=x.Version,CachedAtUtc=DateTimeOffset.UtcNow
            }));

            foreach(LocalOperation op in await store.PendingAsync())
            {
                op.State=nameof(LocalSyncState.Sending);op.LastAttemptUtc=DateTimeOffset.UtcNow;op.AttemptCount++;await store.SaveOperationAsync(op);
                try
                {
                    OperationResultDto result=await api.SendAsync(op,ct);
                    op.State=result.State=="Conflict"?nameof(LocalSyncState.Conflict):nameof(LocalSyncState.Acknowledged);
                    op.LastError=result.Message;
                    if(op.AssignmentId is Guid id && result.ServerVersion is long version)
                    {
                        LocalAssignment? assignment=await store.GetAssignmentAsync(id);
                        if(assignment is not null){assignment.Version=version;assignment.Status=OperationStatus(op.Kind,op.Category,assignment.Status);await store.ReplaceAssignmentsAsync([assignment]);}
                    }
                }
                catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException)
                {
                    op.State=nameof(LocalSyncState.Failed);op.LastError=ex.Message;await store.SaveOperationAsync(op);State="Waiting";return;
                }
                await store.SaveOperationAsync(op);
            }

            foreach(LocalLocationObservation loc in await store.PendingLocationsAsync())
            {
                LocalOperation locationOperation=new(){OperationId=loc.ObservationId,Kind="LocationObservation",AssignmentId=loc.AssignmentId,OccurredAtUtc=loc.ObservedAtUtc,DeviceId="location",LocationObservationId=loc.ObservationId,Latitude=loc.Latitude,Longitude=loc.Longitude,AccuracyMetres=loc.AccuracyMetres,LocationObservedAtUtc=loc.ObservedAtUtc,LocationSource=loc.Source};
                try{await api.SendAsync(locationOperation,ct);loc.State=nameof(LocalSyncState.Acknowledged);await store.UpdateLocationAsync(loc);}
                catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException){loc.State=nameof(LocalSyncState.Failed);await store.UpdateLocationAsync(loc);State="Waiting";return;}
            }

            await uploads.ProcessAsync(ct);
            LastSuccessUtc=DateTimeOffset.UtcNow;State="Healthy";
        }
        finally{gate.Release();}
    }

    private static string OperationStatus(string kind,string? category,string current)=>kind switch{"AcceptAssignment"=>"Accepted","Arrive"=>"Arrived","StartSession"=>"InProgress","CompleteSession"=>"Completed","SetExceptionStatus" when !string.IsNullOrWhiteSpace(category)=>category!,_=>current};
}

public sealed record WorkDayBootstrapDto(string UserName,string DisplayName,DateTimeOffset ServerTimeUtc,IReadOnlyList<AssignmentDto> Today,IReadOnlyList<AssignmentDto> Upcoming,WorkSessionDto? ActiveWorkSession,string LocationTrackingMode,DateTimeOffset GeneratedAtUtc);
public sealed record AssignmentDto(Guid Id,Guid? JobId,Guid? BookingId,string Title,DateTimeOffset StartsAtUtc,DateTimeOffset EndsAtUtc,string? Location,string Status,string? Instructions,Guid? WorkspaceId,Guid? ConversationId,string? CustomerName,string? CustomerEmail,string? CustomerPhone,long Version);
public sealed record WorkSessionDto(Guid Id,string UserName,DateTimeOffset StartedAtUtc,DateTimeOffset? EndedAtUtc,string DeviceId,bool StartedOffline,bool? EndedOffline,string Source,DateTimeOffset UpdatedAtUtc);
public sealed record OperationResultDto(Guid OperationId,string State,DateTimeOffset AcknowledgedAtUtc,string? Message,long? ServerVersion);
public sealed record MessageThreadDto(Guid Id,string ScopeType,Guid? ScopeId,string Title,string CreatedBy,DateTimeOffset CreatedAtUtc,DateTimeOffset? LastMessageAtUtc,long UnreadCount);
public sealed record MessageDto(Guid Id,Guid ThreadId,string SenderUserName,string Body,DateTimeOffset CreatedAtUtc);
public sealed record NotificationDto(Guid Id,string UserName,string Channel,string EventType,string Subject,string Body,string? ReferenceType,string? ReferenceId,string Status,int Attempts,string? LastError,DateTimeOffset CreatedAtUtc,DateTimeOffset? DeliveredAtUtc);
public sealed record GalleryDto(Guid Id,Guid WorkspaceId,string Title,string? Description,string PublicToken,bool IsPublished,DateTimeOffset? PublishedAtUtc,DateTimeOffset CreatedAtUtc,DateTimeOffset UpdatedAtUtc,IReadOnlyList<object> Items);
public sealed record SendingIdentityOptionDto(string Type,string Label,string FromAddress,bool IsDefault);
public sealed record SendingIdentityOptionsDto(string DefaultIdentity,bool CanSendAsSelf,IReadOnlyList<SendingIdentityOptionDto> Identities);

public sealed record DeviceSessionDto(Guid SessionId,string Token,DateTimeOffset ExpiresAtUtc);

public sealed record WorkDayClientObservationDto(string DeviceId,DateTimeOffset ObservedAtUtc,string Connectivity,string SynchronizationState,int PendingOperations,DateTimeOffset? OldestPendingAtUtc,int PendingUploads,int FailedUploads,bool WorkSessionActive,string LocationPermissionState);
