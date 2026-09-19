using System.Net.Http.Json;

namespace Aegis.Studio.WorkDay;

public sealed class WorkDayApiClient
{
    private readonly HttpClient http=new(){Timeout=TimeSpan.FromSeconds(15)};
    public void SetServer(Uri server)=>http.BaseAddress=server;
    public async Task LoginAsync(string user,string password,CancellationToken ct=default){using HttpResponseMessage r=await http.PostAsJsonAsync("api/auth/login",new{userName=user,password},ct);r.EnsureSuccessStatusCode();}
    public Task<WorkDayBootstrapDto?> BootstrapAsync(CancellationToken ct=default)=>http.GetFromJsonAsync<WorkDayBootstrapDto>("api/workday/bootstrap",ct);
    public async Task<OperationResultDto> SendAsync(LocalOperation op,CancellationToken ct=default)
    {
        using HttpResponseMessage r=await http.PostAsJsonAsync("api/workday/operations",new{operationId=op.OperationId,kind=op.Kind,assignmentId=op.AssignmentId,occurredAtUtc=op.OccurredAtUtc,deviceId=op.DeviceId,text=op.Text,category=op.Category,location=(object?)null,expectedVersion=op.ExpectedVersion},ct);
        OperationResultDto? result=await r.Content.ReadFromJsonAsync<OperationResultDto>(cancellationToken:ct);
        if(result is null)throw new InvalidOperationException("Studio did not return an operation acknowledgement.");
        return result;
    }
}

public sealed record WorkDayBootstrapDto(string UserName,string DisplayName,DateTimeOffset ServerTimeUtc,IReadOnlyList<AssignmentDto> Today,IReadOnlyList<AssignmentDto> Upcoming,WorkSessionDto? ActiveWorkSession,string LocationTrackingMode,DateTimeOffset GeneratedAtUtc);
public sealed record AssignmentDto(Guid Id,Guid? JobId,Guid? BookingId,string Title,DateTimeOffset StartsAtUtc,DateTimeOffset EndsAtUtc,string? Location,string Status,string? Instructions,Guid? WorkspaceId,string? CustomerName,string? CustomerEmail,string? CustomerPhone,long Version);
public sealed record WorkSessionDto(Guid Id,string UserName,DateTimeOffset StartedAtUtc,DateTimeOffset? EndedAtUtc,string DeviceId,bool StartedOffline,bool? EndedOffline,string Source,DateTimeOffset UpdatedAtUtc);
public sealed record OperationResultDto(Guid OperationId,string State,DateTimeOffset AcknowledgedAtUtc,string? Message,long? ServerVersion);

public sealed class WorkDaySyncEngine(WorkDayLocalStore store,WorkDayApiClient api)
{
    private readonly SemaphoreSlim gate=new(1,1);
    public DateTimeOffset? LastAttemptUtc{get;private set;} public DateTimeOffset? LastSuccessUtc{get;private set;} public string State{get;private set;}="Unknown";
    public async Task SynchronizeAsync(CancellationToken ct=default)
    {
        if(!await gate.WaitAsync(0,ct))return;
        try
        {
            LastAttemptUtc=DateTimeOffset.UtcNow;State="Sending";
            WorkDayBootstrapDto? bootstrap=await api.BootstrapAsync(ct);
            if(bootstrap is null)throw new InvalidOperationException("Studio returned no WorkDay bootstrap.");
            await store.ReplaceAssignmentsAsync(bootstrap.Today.Concat(bootstrap.Upcoming).Select(x=>new LocalAssignment{Id=x.Id,JobId=x.JobId,BookingId=x.BookingId,Title=x.Title,StartsAtUtc=x.StartsAtUtc,EndsAtUtc=x.EndsAtUtc,Location=x.Location,Status=x.Status,Instructions=x.Instructions,CustomerName=x.CustomerName,CustomerEmail=x.CustomerEmail,CustomerPhone=x.CustomerPhone,Version=x.Version,CachedAtUtc=DateTimeOffset.UtcNow}));
            foreach(LocalOperation op in await store.PendingAsync())
            {
                op.State=nameof(LocalSyncState.Sending);op.LastAttemptUtc=DateTimeOffset.UtcNow;op.AttemptCount++;await store.SaveOperationAsync(op);
                try
                {
                    OperationResultDto result=await api.SendAsync(op,ct);
                    op.State=result.State=="Conflict"?nameof(LocalSyncState.Conflict):nameof(LocalSyncState.Acknowledged);
                    op.LastError=result.Message;
                }
                catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException)
                {
                    op.State=nameof(LocalSyncState.Failed);op.LastError=ex.Message;await store.SaveOperationAsync(op);State="Waiting";return;
                }
                await store.SaveOperationAsync(op);
            }
            LastSuccessUtc=DateTimeOffset.UtcNow;State="Healthy";
        }
        finally{gate.Release();}
    }
}
