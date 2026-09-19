using SQLite;

namespace Aegis.Studio.WorkDay;

public enum LocalSyncState { Pending, Sending, Acknowledged, Failed, Conflict }

public sealed class LocalAssignment
{
    [PrimaryKey] public Guid Id { get; set; }
    public Guid? JobId { get; set; }
    public Guid? BookingId { get; set; }
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

public sealed class WorkDayLocalStore
{
    private readonly SQLiteAsyncConnection db;
    public WorkDayLocalStore()
    {
        string path=Path.Combine(FileSystem.AppDataDirectory,"workday-v1.db3");
        db=new SQLiteAsyncConnection(path,SQLiteOpenFlags.ReadWrite|SQLiteOpenFlags.Create|SQLiteOpenFlags.FullMutex);
    }
    public async Task InitializeAsync(){await db.CreateTableAsync<LocalAssignment>();await db.CreateTableAsync<LocalOperation>();await db.CreateTableAsync<LocalWorkSession>();}
    public async Task ReplaceAssignmentsAsync(IEnumerable<LocalAssignment> values){await InitializeAsync();foreach(LocalAssignment v in values)await db.InsertOrReplaceAsync(v);}
    public async Task<List<LocalAssignment>> GetAssignmentsAsync()=>await db.Table<LocalAssignment>().OrderBy(x=>x.StartsAtUtc).ToListAsync();
    public async Task QueueAsync(LocalOperation operation){await InitializeAsync();await db.InsertOrReplaceAsync(operation);}
    public async Task<List<LocalOperation>> PendingAsync()=>await db.Table<LocalOperation>().Where(x=>x.State==nameof(LocalSyncState.Pending)||x.State==nameof(LocalSyncState.Failed)).OrderBy(x=>x.OccurredAtUtc).ToListAsync();
    public Task SaveOperationAsync(LocalOperation operation)=>db.InsertOrReplaceAsync(operation);
    public Task SaveWorkSessionAsync(LocalWorkSession session)=>db.InsertOrReplaceAsync(session);
    public async Task<LocalWorkSession?> ActiveSessionAsync()=>await db.Table<LocalWorkSession>().Where(x=>x.EndedAtUtc==null).OrderByDescending(x=>x.StartedAtUtc).FirstOrDefaultAsync();
}
