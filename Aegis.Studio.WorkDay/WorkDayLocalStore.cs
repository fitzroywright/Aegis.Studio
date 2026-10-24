using Aegis.Studio.WorkDay.Core;
using SQLite;

namespace Aegis.Studio.WorkDay;

public sealed class LocalAssignment
{
    public string? MediaDestinationJson { get; set; }
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
    public string? ClientName { get; set; }
    public string? ClientEmail { get; set; }
    public string? ClientPhone { get; set; }
    public bool IsCurrent { get; set; }=true;
    [Ignore] public string? FieldTargetsJson { get; set; }
    public string? FieldTargetsCiphertext { get; set; }
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
    [Ignore] public string? Category { get; set; }
    public string? CategoryCiphertext { get; set; }
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
    public DateTimeOffset? ResolvedAtUtc { get; set; }
    public string? ResolutionCiphertext { get; set; }
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
    public string? DestinationJson { get; set; }
    public Guid? ServerVersionId { get; set; }
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

public sealed class LocalAvailability
{
    [PrimaryKey] public Guid Id { get; set; }
    public string UserName { get; set; }="";
    public DateTimeOffset StartsAtUtc { get; set; }
    public DateTimeOffset EndsAtUtc { get; set; }
    public string AvailabilityType { get; set; }="";
    [Ignore] public string? Note { get; set; }
    public string? NoteCiphertext { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string SyncState { get; set; }=nameof(LocalSyncState.Acknowledged);
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public string? ResolutionCiphertext { get; set; }
    public DateTimeOffset? ResolvedAtUtc { get; set; }
}

public sealed class LocalMessageSubmission
{
    [PrimaryKey] public Guid OperationId { get; set; }
    public Guid TargetId { get; set; }
    public string TargetType { get; set; }="";
    [Ignore] public string Body { get; set; }="";
    public string? BodyCiphertext { get; set; }
    public string? SendingIdentity { get; set; }
    public bool IsInternal { get; set; }
    public string State { get; set; }="Pending";
    public int AttemptCount { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? LastAttemptUtc { get; set; }
    public string? LastError { get; set; }
}

public sealed class CachedMessageThread
{
    [PrimaryKey] public Guid Id { get; set; }
    public string ScopeType { get; set; }="";
    public Guid? ScopeId { get; set; }
    [Ignore] public string Title { get; set; }="";
    public string? TitleCiphertext { get; set; }
    public string CreatedBy { get; set; }="";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? LastMessageAtUtc { get; set; }
    public long UnreadCount { get; set; }
}

public sealed class CachedThreadMessage
{
    [PrimaryKey] public Guid Id { get; set; }
    public Guid ThreadId { get; set; }
    public string SenderUserName { get; set; }="";
    [Ignore] public string Body { get; set; }="";
    public string? BodyCiphertext { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class LocalMetadata
{
    [PrimaryKey] public string Key { get; set; }="";
    public string Value { get; set; }="";
}

public sealed class LocalLocationObservation
{
    [PrimaryKey] public Guid ObservationId { get; set; }
    public Guid? AssignmentId { get; set; }
    public Guid? WorkSessionId { get; set; }
    [Ignore] public double Latitude { get; set; }
    [Ignore] public double Longitude { get; set; }
    [Ignore] public double? AccuracyMetres { get; set; }
    // Compatibility columns preserve upgradeability from the pre-release schema.
    // They are always scrubbed to non-sensitive values; encrypted fields are authoritative.
    [Column("Latitude")] public double LegacyLatitude { get; set; }
    [Column("Longitude")] public double LegacyLongitude { get; set; }
    [Column("AccuracyMetres")] public double? LegacyAccuracyMetres { get; set; }
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
    private readonly SemaphoreSlim initialization=new(1,1);
    private bool initialized;
    private readonly WorkDayDataProtector protector;
    public WorkDayLocalStore(WorkDayDataProtector protector)
    {
        this.protector=protector;
        string path=Path.Combine(FileSystem.AppDataDirectory,"workday-v1.db3");
        db=new SQLiteAsyncConnection(path,SQLiteOpenFlags.ReadWrite|SQLiteOpenFlags.Create|SQLiteOpenFlags.FullMutex);
    }

    public async Task InitializeAsync()
    {
        if(initialized)return;
        await initialization.WaitAsync();
        try
        {
            if(initialized)return;
        await db.CreateTableAsync<LocalMetadata>();
        await db.CreateTableAsync<LocalAssignment>();
        await db.CreateTableAsync<LocalOperation>();
        await db.CreateTableAsync<LocalWorkSession>();
        await db.CreateTableAsync<LocalUpload>();
        await db.CreateTableAsync<LocalAvailability>();
        await db.CreateTableAsync<LocalMessageSubmission>();
        await db.CreateTableAsync<CachedMessageThread>();
        await db.CreateTableAsync<CachedThreadMessage>();
        await db.CreateTableAsync<LocalLocationObservation>();
        await MigrateAsync();
            initialized=true;
        }
        finally{initialization.Release();}
    }

    private static string AccountServer(Uri server)=>server.Host.Equals("api.bryanstudiosltd.com",StringComparison.OrdinalIgnoreCase)
        ?"https://studio.bryanstudiosltd.com":server.GetLeftPart(UriPartial.Authority);
    private static string AccountServer(string server)=>Uri.TryCreate(server,UriKind.Absolute,out Uri? parsed)?AccountServer(parsed):server.TrimEnd('/');

    public async Task BindAccountAsync(Uri server,string userName)
    {
        await InitializeAsync();
        string accountServer=AccountServer(server);string scope=accountServer+"|"+userName.Trim().ToLowerInvariant();
        LocalMetadata? current=await db.Table<LocalMetadata>().Where(x=>x.Key=="AccountScope").FirstOrDefaultAsync();
        if(current is not null&&current.Value!=scope)
            throw new InvalidOperationException("This device contains another Studio account's offline data. Recover that account's reports before changing device enrollment.");
        if(current is null)
        {
            string previousUser=Preferences.Default.Get("WorkDay.UserName","");
            string previousServer=Preferences.Default.Get("WorkDay.ServerUrl","").TrimEnd('/');
            if(previousUser.Length>0&&(!previousUser.Equals(userName,StringComparison.OrdinalIgnoreCase)||AccountServer(previousServer)!=accountServer))
                throw new InvalidOperationException("Recover the previous account's cached reports before changing this device's Studio account.");
            await db.InsertOrReplaceAsync(new LocalMetadata{Key="AccountScope",Value=scope});
        }
    }

    public async Task<string?> GetMetadataAsync(string key)
    {await InitializeAsync();return (await db.Table<LocalMetadata>().Where(x=>x.Key==key).FirstOrDefaultAsync())?.Value;}
    public async Task SetMetadataAsync(string key,string value)
    {await InitializeAsync();await db.InsertOrReplaceAsync(new LocalMetadata{Key=key,Value=value});}

    private async Task MigrateAsync()
    {
        LocalMetadata? row=await db.Table<LocalMetadata>().Where(x=>x.Key=="SchemaVersion").FirstOrDefaultAsync();
        int version=int.TryParse(row?.Value,out int parsed)?parsed:1;
        if(version<2)
        {
            await MigrateSensitiveQueueFieldsAsync();
            version=2;
        }
        if(version<3)
        {
            await db.ExecuteAsync("update LocalAssignment set IsCurrent=1");
            await db.ExecuteAsync("update LocalAvailability set SyncState='Acknowledged' where SyncState is null");
            version=3;
        }
        if(version<4)
        {
            var columns=await db.GetTableInfoAsync(nameof(LocalOperation));
            if(columns.Any(x=>x.Name.Equals("Category",StringComparison.OrdinalIgnoreCase)))
            {
                foreach(var old in await db.QueryAsync<LegacyCategory>("select OperationId,Category from LocalOperation where Category is not null"))
                {
                    var encrypted=await protector.ProtectAsync(old.Category);
                    await db.ExecuteAsync("update LocalOperation set CategoryCiphertext=?,Category=null where OperationId=?",encrypted,old.OperationId);
                }
            }
            version=4;
        }
        await db.InsertOrReplaceAsync(new LocalMetadata{Key="SchemaVersion",Value=version.ToString(System.Globalization.CultureInfo.InvariantCulture)});
    }

    private async Task MigrateSensitiveQueueFieldsAsync()
    {
        IReadOnlyList<SQLiteConnection.ColumnInfo> opColumns=await db.GetTableInfoAsync(nameof(LocalOperation));
        HashSet<string> opNames=opColumns.Select(x=>x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(opNames.Contains("Text"))
        {
            List<LegacyOperationSensitive> legacy=await db.QueryAsync<LegacyOperationSensitive>(
                "select OperationId, Text, Latitude, Longitude, AccuracyMetres from LocalOperation");
            foreach(LegacyOperationSensitive old in legacy)
            {
                LocalOperation? current=await db.Table<LocalOperation>().Where(x=>x.OperationId==old.OperationId).FirstOrDefaultAsync();
                if(current is null) continue;
                if(string.IsNullOrWhiteSpace(current.TextCiphertext)&&old.Text is not null) current.Text=old.Text;
                if(string.IsNullOrWhiteSpace(current.LatitudeCiphertext)&&old.Latitude.HasValue) current.Latitude=old.Latitude;
                if(string.IsNullOrWhiteSpace(current.LongitudeCiphertext)&&old.Longitude.HasValue) current.Longitude=old.Longitude;
                if(string.IsNullOrWhiteSpace(current.AccuracyCiphertext)&&old.AccuracyMetres.HasValue) current.AccuracyMetres=old.AccuracyMetres;
                await ProtectOperationAsync(current);
                await db.InsertOrReplaceAsync(current);
            }
            await db.ExecuteAsync("update LocalOperation set Text=null, Latitude=null, Longitude=null, AccuracyMetres=null");
        }

        IReadOnlyList<SQLiteConnection.ColumnInfo> locationColumns=await db.GetTableInfoAsync(nameof(LocalLocationObservation));
        HashSet<string> locationNames=locationColumns.Select(x=>x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(locationNames.Contains("Latitude")&&locationNames.Contains("Longitude"))
        {
            List<LegacyLocationSensitive> legacy=await db.QueryAsync<LegacyLocationSensitive>(
                "select ObservationId, Latitude, Longitude, AccuracyMetres from LocalLocationObservation");
            foreach(LegacyLocationSensitive old in legacy)
            {
                LocalLocationObservation? current=await db.Table<LocalLocationObservation>().Where(x=>x.ObservationId==old.ObservationId).FirstOrDefaultAsync();
                if(current is null) continue;
                if(string.IsNullOrWhiteSpace(current.LatitudeCiphertext)) current.Latitude=old.Latitude;
                if(string.IsNullOrWhiteSpace(current.LongitudeCiphertext)) current.Longitude=old.Longitude;
                if(string.IsNullOrWhiteSpace(current.AccuracyCiphertext)&&old.AccuracyMetres.HasValue) current.AccuracyMetres=old.AccuracyMetres;
                await ProtectLocationAsync(current);
                current.LegacyLatitude=0d;
                current.LegacyLongitude=0d;
                current.LegacyAccuracyMetres=null;
                await db.InsertOrReplaceAsync(current);
            }
            await db.ExecuteAsync("update LocalLocationObservation set Latitude=0, Longitude=0, AccuracyMetres=null");
        }
    }

    private sealed class LegacyCategory { public Guid OperationId {get;set;} public string? Category {get;set;} }

    private sealed class LegacyOperationSensitive
    {
        public Guid OperationId { get; set; }
        public string? Text { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public double? AccuracyMetres { get; set; }
    }

    private sealed class LegacyLocationSensitive
    {
        public Guid ObservationId { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double? AccuracyMetres { get; set; }
    }

    public async Task ReplaceAssignmentsAsync(IEnumerable<LocalAssignment> values,bool authoritative=false)
    {
        await InitializeAsync();
        List<LocalAssignment> prepared=[];
        foreach(LocalAssignment value in values)
        {
            LocalAssignment v=new()
            {
                Id=value.Id,JobId=value.JobId,BookingId=value.BookingId,WorkspaceId=value.WorkspaceId,ConversationId=value.ConversationId,
                Title=await protector.ProtectAsync(value.Title)??"",StartsAtUtc=value.StartsAtUtc,EndsAtUtc=value.EndsAtUtc,
                Location=await protector.ProtectAsync(value.Location),Status=value.Status,Instructions=await protector.ProtectAsync(value.Instructions),
                ClientName=await protector.ProtectAsync(value.ClientName),ClientEmail=await protector.ProtectAsync(value.ClientEmail),
                FieldTargetsCiphertext=await protector.ProtectAsync(value.FieldTargetsJson),ClientPhone=await protector.ProtectAsync(value.ClientPhone),IsCurrent=value.IsCurrent,Version=value.Version,CachedAtUtc=value.CachedAtUtc
            };
            prepared.Add(v);
        }
        await db.RunInTransactionAsync(connection=>
        {
            if(authoritative)connection.Execute("update LocalAssignment set IsCurrent=0");
            foreach(LocalAssignment value in prepared)connection.InsertOrReplace(value);
        });
        DateTimeOffset cutoff=DateTimeOffset.UtcNow.AddDays(-14);
        IReadOnlyList<Guid> protectedIds=(await UnacknowledgedAsync()).Where(x=>x.AssignmentId.HasValue).Select(x=>x.AssignmentId!.Value)
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

    public async Task ReplaceAvailabilityAsync(IEnumerable<LocalAvailability> values)
    {
        await InitializeAsync();
        HashSet<Guid> outstanding=(await GetAvailabilityAsync()).Where(x=>WorkDayClientRules.IsOutstanding(x.SyncState)).Select(x=>x.Id).ToHashSet();
        List<LocalAvailability> prepared=[];
        foreach(LocalAvailability value in values)
        {
            if(outstanding.Contains(value.Id))continue;
            value.NoteCiphertext=await protector.ProtectAsync(value.Note);
            prepared.Add(value);
        }
        await db.RunInTransactionAsync(connection=>
        {
            connection.Execute("delete from LocalAvailability where SyncState='Acknowledged'");
            foreach(LocalAvailability value in prepared)
                if(connection.Table<LocalAvailability>().Where(x=>x.Id==value.Id).FirstOrDefault()?.SyncState!="Resolved")connection.InsertOrReplace(value);
        });
    }

    public async Task SaveAvailabilityAsync(LocalAvailability value)
    {
        await InitializeAsync();value.NoteCiphertext=await protector.ProtectAsync(value.Note);
        await db.InsertOrReplaceAsync(value);
    }

    public async Task ResolveAvailabilityConflictAsync(Guid id,string reason)
    {
        if(string.IsNullOrWhiteSpace(reason)||reason.Length>4000)throw new InvalidOperationException("Record a resolution reason.");
        string resolution=await protector.ProtectAsync(reason.Trim())??"";
        await db.RunInTransactionAsync(connection=>
        {
            var value=connection.Table<LocalAvailability>().Where(x=>x.Id==id).FirstOrDefault();
            if(value?.SyncState!="Conflict")throw new InvalidOperationException("Only a rejected availability change can be resolved locally.");
            value.SyncState="Resolved";value.ResolvedAtUtc=DateTimeOffset.UtcNow;value.ResolutionCiphertext=resolution;connection.Update(value);
        });
    }

    public async Task ResolveAssignmentConflictAsync(Guid assignmentId,string reason)
    {
        if(string.IsNullOrWhiteSpace(reason)||reason.Length>4000)throw new InvalidOperationException("Record how the Job Owner resolved these reports (at most 4000 characters).");
        string resolution=await protector.ProtectAsync(reason.Trim())??"";
        await db.RunInTransactionAsync(connection=>
        {
            var outstanding=connection.Table<LocalOperation>().Where(x=>x.AssignmentId==assignmentId&&x.State!="Acknowledged"&&x.State!="Resolved").ToList();
            if(!outstanding.Any(x=>x.State=="Conflict"))throw new InvalidOperationException("Refresh the conflicting assignment first.");
            if(outstanding.Any(x=>!WorkDayClientRules.CanResolveReport(x.State,x.AttemptCount)))
                throw new InvalidOperationException("A report has an uncertain server result. Synchronize its original operation before resolving the conflict.");
            foreach(var op in outstanding)
            {
                op.State=nameof(LocalSyncState.Resolved);op.ResolvedAtUtc=DateTimeOffset.UtcNow;op.ResolutionCiphertext=resolution;
                connection.Update(op);
            }
        });
    }

    public async Task<List<LocalOperation>> OperationHistoryAsync(Guid assignmentId)
    {
        var values=await db.Table<LocalOperation>().Where(x=>x.AssignmentId==assignmentId).OrderBy(x=>x.OccurredAtUtc).ToListAsync();
        foreach(var value in values)await UnprotectOperationAsync(value);
        return values;
    }
    public Task<string?> ResolutionTextAsync(LocalOperation operation)=>protector.UnprotectAsync(operation.ResolutionCiphertext);

    public async Task<List<LocalAvailability>> GetAvailabilityAsync()
    {
        List<LocalAvailability> values=await db.Table<LocalAvailability>().OrderBy(x=>x.StartsAtUtc).ToListAsync();
        foreach(LocalAvailability value in values) value.Note=await protector.UnprotectAsync(value.NoteCiphertext);
        return values;
    }

    public async Task QueueAsync(LocalOperation operation)
    {
        await InitializeAsync();
        await ProtectOperationAsync(operation);
        await db.InsertOrReplaceAsync(operation);
    }
    public async Task<List<LocalOperation>> PendingAsync()
    {
        List<LocalOperation> values=await db.Table<LocalOperation>().Where(x=>x.State==nameof(LocalSyncState.Pending)||x.State==nameof(LocalSyncState.Failed)||x.State==nameof(LocalSyncState.Sending)).OrderBy(x=>x.OccurredAtUtc).ToListAsync();
        foreach(LocalOperation value in values) await UnprotectOperationAsync(value);
        return values;
    }
    public async Task<DateTimeOffset?> OldestPendingOperationAsync()=> (await PendingAsync()).FirstOrDefault()?.OccurredAtUtc;
    public async Task<List<LocalOperation>> UnacknowledgedAsync()
    {
        List<LocalOperation> values=await db.Table<LocalOperation>().Where(x=>x.State!=nameof(LocalSyncState.Acknowledged)&&x.State!=nameof(LocalSyncState.Resolved)).OrderBy(x=>x.OccurredAtUtc).ToListAsync();
        foreach(LocalOperation value in values)await UnprotectOperationAsync(value);
        return values;
    }
    public async Task SaveOperationAsync(LocalOperation operation)
    {
        await ProtectOperationAsync(operation);
        await db.InsertOrReplaceAsync(operation);
    }

    public Task SaveWorkSessionAsync(LocalWorkSession session)=>db.InsertOrReplaceAsync(session);
    public async Task<LocalWorkSession?> ActiveSessionAsync()=>await db.Table<LocalWorkSession>().Where(x=>x.EndedAtUtc==null).OrderByDescending(x=>x.StartedAtUtc).FirstOrDefaultAsync();

    public async Task QueueUploadAsync(LocalUpload upload){await InitializeAsync();await db.InsertOrReplaceAsync(upload);}
    public async Task<List<LocalUpload>> PendingUploadsAsync()=>await db.Table<LocalUpload>().Where(x=>x.State==nameof(LocalUploadState.Pending)||x.State==nameof(LocalUploadState.Uploading)||x.State==nameof(LocalUploadState.Failed)||x.State==nameof(LocalUploadState.Retrying)).OrderBy(x=>x.CreatedAtUtc).ToListAsync();
    public async Task<LocalUpload?> GetUploadAsync(Guid id)=>await db.Table<LocalUpload>().Where(x=>x.UploadId==id).FirstOrDefaultAsync();
    public Task SaveUploadAsync(LocalUpload upload)=>db.InsertOrReplaceAsync(upload);
    public async Task<int> PendingUploadCountAsync()=>await db.Table<LocalUpload>().Where(x=>x.State!=nameof(LocalUploadState.Uploaded)).CountAsync();
    public async Task<int> FailedUploadCountAsync()=>await db.Table<LocalUpload>().Where(x=>x.State==nameof(LocalUploadState.Failed)).CountAsync();
    public async Task ReplaceCachedThreadsAsync(IEnumerable<MessageThreadDto> threads)
    {
        await InitializeAsync();
        List<CachedMessageThread> rows=[];
        foreach(MessageThreadDto thread in threads)
            rows.Add(new CachedMessageThread
            {
                Id=thread.Id,ScopeType=thread.ScopeType,ScopeId=thread.ScopeId,
                TitleCiphertext=await protector.ProtectAsync(thread.Title),CreatedBy=thread.CreatedBy,
                CreatedAtUtc=thread.CreatedAtUtc,LastMessageAtUtc=thread.LastMessageAtUtc,UnreadCount=thread.UnreadCount
            });
        await db.RunInTransactionAsync(connection=>
        {
            connection.DeleteAll<CachedMessageThread>();
            foreach(CachedMessageThread row in rows)connection.InsertOrReplace(row);
        });
    }
    public async Task<List<MessageThreadDto>> GetCachedThreadsAsync()
    {
        await InitializeAsync();
        List<CachedMessageThread> rows=await db.Table<CachedMessageThread>().OrderByDescending(x=>x.LastMessageAtUtc).ToListAsync();
        List<MessageThreadDto> result=[];
        foreach(CachedMessageThread row in rows)
            result.Add(new(row.Id,row.ScopeType,row.ScopeId,await protector.UnprotectAsync(row.TitleCiphertext)??"",row.CreatedBy,row.CreatedAtUtc,row.LastMessageAtUtc,row.UnreadCount));
        return result;
    }
    public async Task ReplaceCachedThreadMessagesAsync(Guid threadId,IEnumerable<MessageDto> messages)
    {
        await InitializeAsync();
        List<CachedThreadMessage> rows=[];
        foreach(MessageDto message in messages)
            rows.Add(new CachedThreadMessage{Id=message.Id,ThreadId=threadId,SenderUserName=message.SenderUserName,BodyCiphertext=await protector.ProtectAsync(message.Body),CreatedAtUtc=message.CreatedAtUtc});
        await db.RunInTransactionAsync(connection=>
        {
            connection.Execute("delete from CachedThreadMessage where ThreadId=?",threadId);
            foreach(CachedThreadMessage row in rows)connection.InsertOrReplace(row);
        });
    }
    public async Task<List<MessageDto>> GetCachedThreadMessagesAsync(Guid threadId)
    {
        await InitializeAsync();
        List<CachedThreadMessage> rows=await db.Table<CachedThreadMessage>().Where(x=>x.ThreadId==threadId).OrderBy(x=>x.CreatedAtUtc).ToListAsync();
        List<MessageDto> result=[];
        foreach(CachedThreadMessage row in rows)
            result.Add(new(row.Id,row.ThreadId,row.SenderUserName,await protector.UnprotectAsync(row.BodyCiphertext)??"",row.CreatedAtUtc));
        return result;
    }

    public async Task QueueMessageAsync(LocalMessageSubmission message)
    {
        await InitializeAsync();
        message.BodyCiphertext=await protector.ProtectAsync(message.Body);
        message.Body="";
        message.State="Pending";
        await db.InsertOrReplaceAsync(message);
    }
    public async Task<List<LocalMessageSubmission>> PendingMessagesAsync()
    {
        await InitializeAsync();
        List<LocalMessageSubmission> values=await db.Table<LocalMessageSubmission>()
            .Where(x=>x.State=="Pending"||x.State=="Failed"||x.State=="Sending")
            .OrderBy(x=>x.CreatedAtUtc).ToListAsync();
        foreach(LocalMessageSubmission value in values)
            value.Body=await protector.UnprotectAsync(value.BodyCiphertext)??"";
        return values;
    }
    public async Task<List<LocalMessageSubmission>> UnacknowledgedMessagesAsync()
    {
        await InitializeAsync();
        List<LocalMessageSubmission> values=await db.Table<LocalMessageSubmission>()
            .Where(x=>x.State!="Acknowledged"&&x.State!="Resolved").OrderBy(x=>x.CreatedAtUtc).ToListAsync();
        foreach(LocalMessageSubmission value in values)
            value.Body=await protector.UnprotectAsync(value.BodyCiphertext)??"";
        return values;
    }
    public async Task SaveMessageAsync(LocalMessageSubmission message)
    {
        await InitializeAsync();
        if(!string.IsNullOrEmpty(message.Body))message.BodyCiphertext=await protector.ProtectAsync(message.Body);
        await db.InsertOrReplaceAsync(message);
    }
    public async Task<int> PendingMessageCountAsync()=>
        await db.Table<LocalMessageSubmission>().Where(x=>x.State!="Acknowledged"&&x.State!="Resolved").CountAsync();


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
        value.CategoryCiphertext=await protector.ProtectAsync(value.Category);
        if(value.Text is not null) value.TextCiphertext=await protector.ProtectAsync(value.Text);
        if(value.Latitude.HasValue) value.LatitudeCiphertext=await protector.ProtectAsync(value.Latitude.Value.ToString("R",System.Globalization.CultureInfo.InvariantCulture));
        if(value.Longitude.HasValue) value.LongitudeCiphertext=await protector.ProtectAsync(value.Longitude.Value.ToString("R",System.Globalization.CultureInfo.InvariantCulture));
        if(value.AccuracyMetres.HasValue) value.AccuracyCiphertext=await protector.ProtectAsync(value.AccuracyMetres.Value.ToString("R",System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task UnprotectOperationAsync(LocalOperation value)
    {
        value.Category=await protector.UnprotectAsync(value.CategoryCiphertext);
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
        v.FieldTargetsJson=await protector.UnprotectAsync(v.FieldTargetsCiphertext);
        v.Title=await protector.UnprotectAsync(v.Title)??"";
        v.Location=await protector.UnprotectAsync(v.Location);
        v.Instructions=await protector.UnprotectAsync(v.Instructions);
        v.ClientName=await protector.UnprotectAsync(v.ClientName);
        v.ClientEmail=await protector.UnprotectAsync(v.ClientEmail);
        v.ClientPhone=await protector.UnprotectAsync(v.ClientPhone);
    }
}

