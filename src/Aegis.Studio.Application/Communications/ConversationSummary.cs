using Aegis.Studio.Domain.Communications;

namespace Aegis.Studio.Application.Communications;

public sealed record ConversationSummary(
    Guid Id,
    Guid WorkspaceId,
    Guid? BookingId,
    string ClientEmail,
    string Subject,
    ConversationStatus Status,
    bool IsUnread,
    string? AssignedUserId,
    DateTimeOffset LastActivityUtc,
    string Preview,
    MessageDirection LastDirection);
