namespace Aegis.Studio.Web.WorkDay;

public enum WorkDayOperationKind { AcceptAssignment, Arrive, StartSession, CompleteSession, SetExceptionStatus, AddNote, RecordException, WorkSignIn, WorkSignOut, LocationObservation }
public enum WorkDayOperationState { Pending, Acknowledged, Failed, Conflict }
public enum WorkDayLocationTrackingMode { Disabled, SignInOutOnly, AssignmentEvents, BackgroundDuringWork }

public sealed record WorkDayOperationRequest(
    Guid OperationId,
    WorkDayOperationKind Kind,
    Guid? AssignmentId,
    DateTimeOffset OccurredAtUtc,
    string DeviceId,
    string? Text,
    string? Category,
    WorkDayLocationEvidence? Location,
    long? ExpectedVersion);

public sealed record WorkDayLocationEvidence(
    Guid ObservationId,
    double Latitude,
    double Longitude,
    double? AccuracyMetres,
    DateTimeOffset ObservedAtUtc,
    string? Source,
    bool? MockLocation);

public sealed record WorkDayOperationResult(
    Guid OperationId,
    WorkDayOperationState State,
    DateTimeOffset AcknowledgedAtUtc,
    string? Message,
    long? ServerVersion);

public sealed record WorkDayWorkSession(
    Guid Id,
    string UserName,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    string DeviceId,
    bool StartedOffline,
    bool? EndedOffline,
    WorkDayLocationEvidence? StartLocation,
    WorkDayLocationEvidence? EndLocation,
    string Source,
    DateTimeOffset UpdatedAtUtc);

public sealed record WorkDayAssignment(
    Guid Id,
    Guid? JobId,
    Guid? BookingId,
    string Title,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    string? Location,
    string Status,
    string? Instructions,
    Guid? WorkspaceId,
    Guid? ConversationId,
    string? CustomerName,
    string? CustomerEmail,
    string? CustomerPhone,
    long Version);

public sealed record WorkDayBootstrap(
    string UserName,
    string DisplayName,
    DateTimeOffset ServerTimeUtc,
    IReadOnlyList<WorkDayAssignment> Today,
    IReadOnlyList<WorkDayAssignment> Upcoming,
    WorkDayWorkSession? ActiveWorkSession,
    WorkDayLocationTrackingMode LocationTrackingMode,
    DateTimeOffset GeneratedAtUtc);

public sealed record WorkDayUploadResult(Guid UploadId,Guid AttachmentId,Guid AssignmentId,string FileName,long SizeBytes,string StorageStatus,DateTimeOffset AcknowledgedAtUtc);
public sealed record WorkDayDiagnosticsSnapshot(
    string Connectivity,
    string SynchronizationState,
    long PendingOperations,
    long FailedOperations,
    DateTimeOffset? OldestPendingAtUtc,
    long PendingUploads,
    long FailedUploads,
    bool WorkSessionActive,
    DateTimeOffset ObservedAtUtc);
public sealed record WorkDayTimeline(Guid WorkSessionId,string UserName,DateTimeOffset StartedAtUtc,DateTimeOffset? EndedAtUtc,IReadOnlyList<WorkDayLocationEvidence> LocationEvidence);

public sealed record WorkDayDeviceSessionRequest(string DeviceId);
public sealed record WorkDayDeviceSessionResponse(Guid SessionId,string Token,DateTimeOffset ExpiresAtUtc);
