namespace Aegis.Studio.Application.Communications;

public sealed record InboundMailRouteResult
(
    Guid WorkspaceId,
    string WorkspaceName,
    string RecipientAddress,
    Guid ConversationId,
    Guid? BookingId,
    string ClientEmail,
    string Subject
);
