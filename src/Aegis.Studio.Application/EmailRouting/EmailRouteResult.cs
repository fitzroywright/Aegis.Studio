namespace Aegis.Studio.Application.EmailRouting;

public sealed record EmailRouteResult(
    bool IsRouted,
    Guid? WorkspaceId,
    string? WorkspaceName,
    string? MatchedAddress,
    string? FailureReason);
