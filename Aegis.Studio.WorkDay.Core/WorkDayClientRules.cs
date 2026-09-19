namespace Aegis.Studio.WorkDay.Core;

public enum LocalSyncState { Pending, Sending, Acknowledged, Failed, Conflict }
public enum LocalUploadState { Pending, Uploading, Uploaded, Failed, Retrying }

public static class WorkDayClientRules
{
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
