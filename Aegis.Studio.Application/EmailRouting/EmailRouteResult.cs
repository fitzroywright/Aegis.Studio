namespace Aegis.Studio.Application.EmailRouting;

public sealed record EmailRouteResult(
    bool IsRouted,
    Guid? WorkspaceId,
    string? WorkspaceName,
    string? MatchedAddress,
    string? FailureReason,
    string DestinationType = "Workspace",
    Guid? DestinationId = null,
    string? PersonUserName = null,
    string? DestinationName = null)
{
    public string UserFacingContext
        => !string.IsNullOrWhiteSpace(DestinationName)
            ? DestinationName!
            : !string.IsNullOrWhiteSpace(WorkspaceName)
                ? WorkspaceName!
                : "Aegis Studio";
}
