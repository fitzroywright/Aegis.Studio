using SQLite;

namespace Aegis.Studio.WorkDay;

public enum LocalSyncState { Pending, Sending, Acknowledged, Failed, Conflict }
public enum LocalUploadState { Pending, Uploading, Uploaded, Failed, Retrying }

public sealed class LocalAssignment
{
    [PrimaryKey] public Guid Id { get; set; }
    public Guid? JobId { get; set; }
    public Guid? BookingId { get; set; }
    public Guid? WorkspaceId { get; set; }
    public Guid? ConversationId { get; set; }
    public string Title { get; set; }="";
    public DateTimeOffset StartsAtUtc { get; set; }
    public DateTimeOffset EndsAtUtc { get; set; }
    public string? Location { get; set; }
    public string Status { get; set; }="";
    public string? Instructions { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerEmail { get; set; }
    public string? CustomerPhone { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CachedAtUtc { get; set; }
}

public sealed class LocalOperation
{
    [PrimaryKey] public Guid OperationId { get; set; }
    public string Kind { get; set; }="";
    public Guid? AssignmentId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public string DeviceId { get; set; }="";
    public string? Text { get; set; }
    public string? Category { get; set; }
    public long? ExpectedVersion { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public double? AccuracyMetres { get; set; }
    public DateTimeOffset? LocationObservedAtUtc { get; set; }
    public string? LocationSource { get; set; }
    public string State { get; set; }=nameof(LocalSyncState.Pending);
    public int AttemptCount { get; set; }
    public DateTimeOffset? LastAttemptUtc { get; set; }
    public string? LastError { get; set; }
}

public sealed class LocalWorkSession
{
    [PrimaryKey] public Guid Id { get; set; }
    public string UserName { get; set; }="";
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? EndedAtUtc { get; set; }
    public string DeviceId { get; set; }="";
    public bool StartedOffline { get; set; }
    public bool? EndedOffline { get; set; }
}

public sealed class LocalUpload
{
    [PrimaryKey] public Guid UploadId { get; set; }
    public Guid AssignmentId { get; set; }
    public string LocalPath { get; set; }="";
    public string FileName { get; set; }="";
    public string ContentType { get; set; }="application/octet-stream";
    public long SizeBytes { get; set; }
    public string State { get; set; }=nameof(LocalUploadState.Pending);
    public int AttemptCount { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? LastAttemptUtc { get; set; }
    public string? LastError { get; set; }
    public Guid? ServerAttachmentId { get; set; }
}

public sealed class LocalLocationObservation
{
    [PrimaryKey] public Guid ObservationId { get; set; }
    public Guid? AssignmentId { get; set; }
    public Guid? WorkSessionId { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double? AccuracyMetres { get; set; }
    public DateTimeOffset ObservedAtUtc { get; set; }
    public string? Source { get; set; }
    public string State { get; set; }=nameof(LocalSyncState.Pending);
}

public sealed class WorkDayLocalStore
{
    private readonly SQLiteAsyncConnection db;
    public WorkDayLocalStore()
    {
        string path=Path.Combine(FileSystem.AppDataDirectory,"workday-v1.db3");
        db=new SQLiteAsyncConnection(path,SQLiteOpenFlags.ReadWrite|SQLiteOpenFlags.Create|SQLiteOpenFlags.FullMutex);
    }

    public async Task InitializeAsync()
    {
        await db.CreateTableAsync<LocalAssignment>();
        await db.CreateTableAsync<LocalOperation>();
        await db.CreateTableAsync<LocalWorkSession>();
        await db.CreateTableAsync<LocalUpload>();
        await db.CreateTableAsync<LocalLocationObservation>();
    }

    public async Task ReplaceAssignmentsAsync(IEnumerable<LocalAssignment> values)
    {
        await InitializeAsync();
        foreach(LocalAssignment v in values) await db.InsertOrReplaceAsync(v);
        DateTimeOffset cutoff=DateTimeOffset.UtcNow.AddDays(-14);
        IReadOnlyList<Guid> protectedIds=(await PendingAsync()).Where(x=>x.AssignmentId.HasValue).Select(x=>x.AssignmentId!.Value)
            .Concat((await PendingUploadsAsync()).Select(x=>x.AssignmentId)).Distinct().ToArray();
        foreach(LocalAssignment old in await db.Table<LocalAssignment>().Where(x=>x.EndsAtUtc<cutoff).ToListAsync())
            if(!protectedIds.Contains(old.Id)) await db.DeleteAsync(old);
    }

    public async Task<List<LocalAssignment>> GetAssignmentsAsync()=>await db.Table<LocalAssignment>().OrderBy(x=>x.StartsAtUtc).ToListAsync();
    public Task<LocalAssignment?> GetAssignmentAsync(Guid id)=>db.Table<LocalAssignment>().Where(x=>x.Id==id).FirstOrDefaultAsync();

    public async Task QueueAsync(LocalOperation operation){await InitializeAsync();await db.InsertOrReplaceAsync(operation);}
    public async Task<List<LocalOperation>> PendingAsync()=>await db.Table<LocalOperation>().Where(x=>x.State==nameof(LocalSyncState.Pending)||x.State==nameof(LocalSyncState.Failed)).OrderBy(x=>x.OccurredAtUtc).ToListAsync();
    public Task SaveOperationAsync(LocalOperation operation)=>db.InsertOrReplaceAsync(operation);

    public Task SaveWorkSessionAsync(LocalWorkSession session)=>db.InsertOrReplaceAsync(session);
    public async Task<LocalWorkSession?> ActiveSessionAsync()=>await db.Table<LocalWorkSession>().Where(x=>x.EndedAtUtc==null).OrderByDescending(x=>x.StartedAtUtc).FirstOrDefaultAsync();

    public async Task QueueUploadAsync(LocalUpload upload){await InitializeAsync();await db.InsertOrReplaceAsync(upload);}
    public async Task<List<LocalUpload>> PendingUploadsAsync()=>await db.Table<LocalUpload>().Where(x=>x.State==nameof(LocalUploadState.Pending)||x.State==nameof(LocalUploadState.Failed)||x.State==nameof(LocalUploadState.Retrying)).OrderBy(x=>x.CreatedAtUtc).ToListAsync();
    public Task SaveUploadAsync(LocalUpload upload)=>db.InsertOrReplaceAsync(upload);
    public async Task<int> PendingUploadCountAsync()=>await db.Table<LocalUpload>().Where(x=>x.State!=nameof(LocalUploadState.Uploaded)).CountAsync();

    public async Task SaveLocationAsync(LocalLocationObservation location){await InitializeAsync();await db.InsertOrReplaceAsync(location);}
    public async Task<List<LocalLocationObservation>> PendingLocationsAsync()=>await db.Table<LocalLocationObservation>().Where(x=>x.State==nameof(LocalSyncState.Pending)||x.State==nameof(LocalSyncState.Failed)).OrderBy(x=>x.ObservedAtUtc).ToListAsync();
    public Task UpdateLocationAsync(LocalLocationObservation location)=>db.InsertOrReplaceAsync(location);
}
