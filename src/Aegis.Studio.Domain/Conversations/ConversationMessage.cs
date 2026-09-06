using Aegis.Studio.Domain.Common;

namespace Aegis.Studio.Domain.Conversations;

public sealed class ConversationMessage : Entity
{
    public ConversationMessage(
        Guid id,
        Guid conversationId,
        ConversationChannel channel,
        MessageVisibility visibility,
        string body,
        DateTimeOffset occurredAt,
        string? externalMessageId = null) : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        ConversationId = conversationId;
        Channel = channel;
        Visibility = visibility;
        Body = body.Trim();
        OccurredAt = occurredAt;
        ExternalMessageId = string.IsNullOrWhiteSpace(externalMessageId) ? null : externalMessageId.Trim();
    }

    public Guid ConversationId { get; }

    public ConversationChannel Channel { get; }

    public MessageVisibility Visibility { get; }

    public string Body { get; }

    public DateTimeOffset OccurredAt { get; }

    public string? ExternalMessageId { get; }
}
