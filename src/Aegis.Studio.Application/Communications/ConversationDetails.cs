using Aegis.Studio.Domain.Communications;

namespace Aegis.Studio.Application.Communications;

public sealed record ConversationDetails(
    Guid Id,
    Guid WorkspaceId,
    Guid? BookingId,
    string ClientEmail,
    string Subject,
    ConversationStatus Status,
    bool IsUnread,
    string? AssignedUserId,
    DateTimeOffset CreatedUtc,
    DateTimeOffset LastActivityUtc);
