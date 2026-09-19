using Aegis.Studio.WorkDay.Core;
using SQLite;

namespace Aegis.Studio.WorkDay;

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
    [Ignore] public string? Text { get; set; }
    public string? TextCiphertext { get; set; }
    public string? Category { get; set; }
    public long? ExpectedVersion { get; set; }
    public Guid? LocationObservationId { get; set; }
    [Ignore] public double? Latitude { get; set; }
    [Ignore] public double? Longitude { get; set; }
    [Ignore] public double? AccuracyMetres { get; set; }
    public string? LatitudeCiphertext { get; set; }
    public string? LongitudeCiphertext { get; set; }
    public string? AccuracyCiphertext { get; set; }
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
    [Ignore] public double Latitude { get; set; }
    [Ignore] public double Longitude { get; set; }
    [Ignore] public double? AccuracyMetres { get; set; }
    public string LatitudeCiphertext { get; set; }="";
    public string LongitudeCiphertext { get; set; }="";
    public string? AccuracyCiphertext { get; set; }
    public DateTimeOffset ObservedAtUtc { get; set; }
    public string? Source { get; set; }
    public string State { get; set; }=nameof(LocalSyncState.Pending);
}

public sealed class WorkDayLocalStore
{
    private readonly SQLiteAsyncConnection db;
    private readonly WorkDayDataProtector protector;
    public WorkDayLocalStore(WorkDayDataProtector protector)
    {
        this.protector=protector;
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
        foreach(LocalAssignment value in values)
        {
            LocalAssignment v=new()
            {
                Id=value.Id,JobId=value.JobId,BookingId=value.BookingId,WorkspaceId=value.WorkspaceId,ConversationId=value.ConversationId,
                Title=await protector.ProtectAsync(value.Title)??"",StartsAtUtc=value.StartsAtUtc,EndsAtUtc=value.EndsAtUtc,
                Location=await protector.ProtectAsync(value.Location),Status=value.Status,Instructions=await protector.ProtectAsync(value.Instructions),
                CustomerName=await protector.ProtectAsync(value.CustomerName),CustomerEmail=await protector.ProtectAsync(value.CustomerEmail),
                CustomerPhone=await protector.ProtectAsync(value.CustomerPhone),Version=value.Version,CachedAtUtc=value.CachedAtUtc
            };
            await db.InsertOrReplaceAsync(v);
        }
        DateTimeOffset cutoff=DateTimeOffset.UtcNow.AddDays(-14);
        IReadOnlyList<Guid> protectedIds=(await PendingAsync()).Where(x=>x.AssignmentId.HasValue).Select(x=>x.AssignmentId!.Value)
            .Concat((await PendingUploadsAsync()).Select(x=>x.AssignmentId)).Distinct().ToArray();
        foreach(LocalAssignment old in await db.Table<LocalAssignment>().Where(x=>x.EndsAtUtc<cutoff).ToListAsync())
            if(!protectedIds.Contains(old.Id)) await db.DeleteAsync(old);
    }

    public async Task<List<LocalAssignment>> GetAssignmentsAsync()
    {
        List<LocalAssignment> values=await db.Table<LocalAssignment>().OrderBy(x=>x.StartsAtUtc).ToListAsync();
        foreach(LocalAssignment v in values) await UnprotectAsync(v);
        return values;
    }
    public async Task<LocalAssignment?> GetAssignmentAsync(Guid id)
    {
        LocalAssignment? value=await db.Table<LocalAssignment>().Where(x=>x.Id==id).FirstOrDefaultAsync();
        if(value is not null) await UnprotectAsync(value);
        return value;
    }

    public async Task QueueAsync(LocalOperation operation)
    {
        await InitializeAsync();
        await ProtectOperationAsync(operation);
        await db.InsertOrReplaceAsync(operation);
    }
    public async Task<List<LocalOperation>> PendingAsync()
    {
        List<LocalOperation> values=await db.Table<LocalOperation>().Where(x=>x.State==nameof(LocalSyncState.Pending)||x.State==nameof(LocalSyncState.Failed)).OrderBy(x=>x.OccurredAtUtc).ToListAsync();
        foreach(LocalOperation value in values) await UnprotectOperationAsync(value);
        return values;
    }
    public async Task<DateTimeOffset?> OldestPendingOperationAsync()=> (await PendingAsync()).FirstOrDefault()?.OccurredAtUtc;
    public async Task SaveOperationAsync(LocalOperation operation)
    {
        await ProtectOperationAsync(operation);
        await db.InsertOrReplaceAsync(operation);
    }

    public Task SaveWorkSessionAsync(LocalWorkSession session)=>db.InsertOrReplaceAsync(session);
    public async Task<LocalWorkSession?> ActiveSessionAsync()=>await db.Table<LocalWorkSession>().Where(x=>x.EndedAtUtc==null).OrderByDescending(x=>x.StartedAtUtc).FirstOrDefaultAsync();

    public async Task QueueUploadAsync(LocalUpload upload){await InitializeAsync();await db.InsertOrReplaceAsync(upload);}
    public async Task<List<LocalUpload>> PendingUploadsAsync()=>await db.Table<LocalUpload>().Where(x=>x.State==nameof(LocalUploadState.Pending)||x.State==nameof(LocalUploadState.Failed)||x.State==nameof(LocalUploadState.Retrying)).OrderBy(x=>x.CreatedAtUtc).ToListAsync();
    public Task SaveUploadAsync(LocalUpload upload)=>db.InsertOrReplaceAsync(upload);
    public async Task<int> PendingUploadCountAsync()=>await db.Table<LocalUpload>().Where(x=>x.State!=nameof(LocalUploadState.Uploaded)).CountAsync();
    public async Task<int> FailedUploadCountAsync()=>await db.Table<LocalUpload>().Where(x=>x.State==nameof(LocalUploadState.Failed)).CountAsync();

    public async Task SaveLocationAsync(LocalLocationObservation location)
    {
        await InitializeAsync();
        await ProtectLocationAsync(location);
        await db.InsertOrReplaceAsync(location);
    }
    public async Task<List<LocalLocationObservation>> PendingLocationsAsync()
    {
        List<LocalLocationObservation> values=await db.Table<LocalLocationObservation>().Where(x=>x.State==nameof(LocalSyncState.Pending)||x.State==nameof(LocalSyncState.Failed)).OrderBy(x=>x.ObservedAtUtc).ToListAsync();
        foreach(LocalLocationObservation value in values) await UnprotectLocationAsync(value);
        return values;
    }
    public async Task UpdateLocationAsync(LocalLocationObservation location)
    {
        await ProtectLocationAsync(location);
        await db.InsertOrReplaceAsync(location);
    }

    private async Task ProtectOperationAsync(LocalOperation value)
    {
        if(value.Text is not null) value.TextCiphertext=await protector.ProtectAsync(value.Text);
        if(value.Latitude.HasValue) value.LatitudeCiphertext=await protector.ProtectAsync(value.Latitude.Value.ToString("R",System.Globalization.CultureInfo.InvariantCulture));
        if(value.Longitude.HasValue) value.LongitudeCiphertext=await protector.ProtectAsync(value.Longitude.Value.ToString("R",System.Globalization.CultureInfo.InvariantCulture));
        if(value.AccuracyMetres.HasValue) value.AccuracyCiphertext=await protector.ProtectAsync(value.AccuracyMetres.Value.ToString("R",System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task UnprotectOperationAsync(LocalOperation value)
    {
        value.Text=await protector.UnprotectAsync(value.TextCiphertext);
        value.Latitude=ParseDouble(await protector.UnprotectAsync(value.LatitudeCiphertext));
        value.Longitude=ParseDouble(await protector.UnprotectAsync(value.LongitudeCiphertext));
        value.AccuracyMetres=ParseDouble(await protector.UnprotectAsync(value.AccuracyCiphertext));
    }

    private async Task ProtectLocationAsync(LocalLocationObservation value)
    {
        value.LatitudeCiphertext=await protector.ProtectAsync(value.Latitude.ToString("R",System.Globalization.CultureInfo.InvariantCulture))??"";
        value.LongitudeCiphertext=await protector.ProtectAsync(value.Longitude.ToString("R",System.Globalization.CultureInfo.InvariantCulture))??"";
        value.AccuracyCiphertext=value.AccuracyMetres.HasValue
            ? await protector.ProtectAsync(value.AccuracyMetres.Value.ToString("R",System.Globalization.CultureInfo.InvariantCulture))
            : null;
    }

    private async Task UnprotectLocationAsync(LocalLocationObservation value)
    {
        value.Latitude=ParseDouble(await protector.UnprotectAsync(value.LatitudeCiphertext))??0d;
        value.Longitude=ParseDouble(await protector.UnprotectAsync(value.LongitudeCiphertext))??0d;
        value.AccuracyMetres=ParseDouble(await protector.UnprotectAsync(value.AccuracyCiphertext));
    }

    private static double? ParseDouble(string? value)=>
        double.TryParse(value,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out double parsed)?parsed:null;

    private async Task UnprotectAsync(LocalAssignment v)
    {
        v.Title=await protector.UnprotectAsync(v.Title)??"";
        v.Location=await protector.UnprotectAsync(v.Location);
        v.Instructions=await protector.UnprotectAsync(v.Instructions);
        v.CustomerName=await protector.UnprotectAsync(v.CustomerName);
        v.CustomerEmail=await protector.UnprotectAsync(v.CustomerEmail);
        v.CustomerPhone=await protector.UnprotectAsync(v.CustomerPhone);
    }
}
