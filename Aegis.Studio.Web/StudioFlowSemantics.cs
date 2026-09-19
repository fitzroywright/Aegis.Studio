namespace Aegis.Studio.Web;

public sealed record StudioFlowStageSnapshot(
    string Name,
    string State,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? Error,
    string? ParentStage,
    string? Branch);

public static class StudioFlowSemantics
{
    public static string[] StageNames(string flowType) => flowType switch
    {
        "InboundMessage" => ["Mailbox", "Router", "Workspace", "Conversation", "Notification"],
        "OutboundMessage" => ["User", "SendingIdentity", "Message", "Provider", "Recipient"],
        "NotificationFanout" => ["Event", "Email", "Slack", "Teams", "InApp"],
        "AttachmentProcessing" => ["Received", "Storage", "Metadata", "Workspace"],
        "GalleryPublish" => ["InternalStorage", "Publish", "PublicGallery", "Verification"],
        "WorkDaySession" => ["Assigned", "Accepted", "Arrived", "InProgress", "Completed", "Synchronized"],
        "WorkDaySync" => ["LocalChange", "Pending", "Sending", "ServerProcessing", "Acknowledged"],
        "WorkDayUpload" => ["Selected", "LocalQueue", "Upload", "Storage", "MetadataAssociation", "Acknowledged"],
        "WorkDayMessage" => ["WorkDay", "Studio", "CommonMessaging", "Provider", "Recipient"],
        _ => []
    };

    public static IReadOnlyList<StudioFlowStageSnapshot> Linear(
        string flowType,
        string currentStage,
        DateTimeOffset observedAtUtc,
        bool failed,
        string? detail = null,
        string? stateOverride = null)
    {
        string[] stages = StageNames(flowType);
        int currentIndex = Array.FindIndex(stages, x => x.Equals(currentStage, StringComparison.OrdinalIgnoreCase));
        if (currentIndex < 0) return [];

        return stages.Select((name, index) => new StudioFlowStageSnapshot(
            name,
            index < currentIndex ? "Completed"
                : index == currentIndex
                    ? !string.IsNullOrWhiteSpace(stateOverride) ? stateOverride
                    : failed ? "Failed" : "Completed"
                : "Pending",
            index == currentIndex ? observedAtUtc : null,
            index == currentIndex && !failed && string.Equals(stateOverride, "Unknown", StringComparison.OrdinalIgnoreCase) == false
                ? observedAtUtc
                : null,
            index == currentIndex && (failed || string.Equals(stateOverride, "Unknown", StringComparison.OrdinalIgnoreCase))
                ? detail
                : null,
            index == 0 ? null : stages[index - 1],
            null)).ToArray();
    }

    public static IReadOnlyList<StudioFlowStageSnapshot> NotificationFanout(
        IReadOnlyDictionary<string, (string State, DateTimeOffset AtUtc, string? Error)> observed,
        DateTimeOffset startedAtUtc)
    {
        string[] stages = StageNames("NotificationFanout");
        return stages.Select(name =>
        {
            bool branch = !name.Equals("Event", StringComparison.OrdinalIgnoreCase);
            bool seen = observed.TryGetValue(name, out var snapshot);
            return new StudioFlowStageSnapshot(
                name,
                seen ? snapshot.State : branch ? "Pending" : "Completed",
                seen ? snapshot.AtUtc : branch ? null : startedAtUtc,
                seen && snapshot.State == "Completed" ? snapshot.AtUtc : branch ? null : startedAtUtc,
                seen ? snapshot.Error : null,
                branch ? "Event" : null,
                branch ? name : null);
        }).ToArray();
    }

    public static string CorrelationId(string? referenceId, Guid durableId) =>
        string.IsNullOrWhiteSpace(referenceId) ? durableId.ToString("D") : referenceId.Trim();
}
