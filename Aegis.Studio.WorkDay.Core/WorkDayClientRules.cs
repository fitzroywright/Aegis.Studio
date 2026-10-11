namespace Aegis.Studio.WorkDay.Core;

public enum LocalSyncState { Pending, Sending, Acknowledged, Failed, Conflict, Resolved }
public enum LocalUploadState { Pending, Uploading, Uploaded, Failed, Retrying }

public static class WorkDayClientRules
{
    public static bool IsAcknowledged(string? state)=>
        string.Equals(state,"Acknowledged",StringComparison.Ordinal);

    // Each dependent offline event uses the version produced by the preceding event.
    public static long NextExpectedVersion(long cachedVersion,int queuedStatusChanges)=>
        checked(cachedVersion+queuedStatusChanges);

    public static long NextExpectedVersionAfterPending(long cachedVersion,IEnumerable<long> pendingVersions)=>
        pendingVersions.Select(x=>checked(x+1)).Append(cachedVersion).Max();

    public static bool ChangesAssignmentStatus(string kind)=>kind is
        "AcceptAssignment" or "DeclineAssignment" or "Arrive" or "StartSession" or
        "CompleteSession" or "SetExceptionStatus";

    public static string AssignmentStatus(string kind,string? category,string current)=>kind switch
    {
        "AcceptAssignment"=>"Accepted", "DeclineAssignment"=>"Declined",
        "Arrive"=>"Arrived", "StartSession"=>"InProgress", "CompleteSession"=>"Completed",
        "SetExceptionStatus" when !string.IsNullOrWhiteSpace(category)=>category!, _=>current
    };

    public static bool IsOutstanding(string? state) => state is not ("Acknowledged" or "Resolved");

    // A conflict response establishes rejection. Reports never attempted can be
    // retained as resolved history; uncertain transport results require replay.
    public static bool CanResolveReport(string state, int attempts) =>
        state == "Conflict" || (attempts == 0 && (state is "Pending" or "Failed"));

    public static bool IsPending(LocalSyncState state) =>
        state is LocalSyncState.Pending or LocalSyncState.Failed;

    public static bool IsUploadPending(LocalUploadState state) =>
        state is LocalUploadState.Pending or LocalUploadState.Failed or LocalUploadState.Retrying;

    public static bool IsUploadSafeOnDevice(LocalUploadState state, bool localFileExists) =>
        state == LocalUploadState.Uploaded || localFileExists;

    public static string ConnectivityState(bool serverReachable, bool localStoreHealthy) =>
        serverReachable ? "Online"
        : localStoreHealthy ? "Offline"
        : "Failed";

    public static string SynchronizationState(bool serverReachable, int pending, int failed) =>
        !serverReachable ? "Waiting"
        : failed > 0 ? "Degraded"
        : pending > 0 ? "Pending"
        : "Healthy";

    public static bool ShouldTrackLocation(string? policy, bool workSessionActive) =>
        workSessionActive &&
        string.Equals(policy, "BackgroundDuringWork", StringComparison.OrdinalIgnoreCase);

    public static bool IsTelemetryStale(DateTimeOffset observedAtUtc, DateTimeOffset nowUtc, TimeSpan staleAfter) =>
        nowUtc - observedAtUtc > staleAfter;

    public static bool ShouldRetainAssignment(
        DateTimeOffset assignmentEndUtc,
        DateTimeOffset nowUtc,
        bool hasPendingOperation,
        bool hasPendingUpload,
        TimeSpan retention)
        => hasPendingOperation || hasPendingUpload || assignmentEndUtc >= nowUtc - retention;

    public static LocalUploadState OnUploadAttemptStarted(LocalUploadState current) =>
        current is LocalUploadState.Pending or LocalUploadState.Failed or LocalUploadState.Retrying
            ? LocalUploadState.Uploading
            : current;

    public static LocalUploadState OnUploadAcknowledged() => LocalUploadState.Uploaded;

    public static LocalUploadState OnUploadTransportFailure(bool localFileExists) =>
        localFileExists ? LocalUploadState.Retrying : LocalUploadState.Failed;

    public static LocalSyncState OnServerResult(bool conflict, bool acknowledged) =>
        conflict ? LocalSyncState.Conflict
        : acknowledged ? LocalSyncState.Acknowledged
        : LocalSyncState.Failed;

    public static bool CanDiscardPendingWork(LocalSyncState operationState, LocalUploadState? uploadState = null) =>
        operationState == LocalSyncState.Acknowledged &&
        (uploadState is null || uploadState == LocalUploadState.Uploaded);
}
