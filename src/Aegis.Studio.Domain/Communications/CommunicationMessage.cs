namespace Aegis.Studio.Domain.Communications;

public sealed class CommunicationMessage
{
    private CommunicationMessage()
    {
    }

    public CommunicationMessage(Guid conversationId, MessageDirection direction, string fromAddress, string toAddress, string subject, string body, string? internetMessageId = null)
    {
        Id = Guid.NewGuid();
        ConversationId = conversationId;
        Direction = direction;
        FromAddress = fromAddress.Trim();
        ToAddress = toAddress.Trim();
        Subject = subject.Trim();
        Body = body;
        InternetMessageId = internetMessageId;
        CreatedUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }
    public Guid ConversationId { get; private set; }
    public MessageDirection Direction { get; private set; }
    public string FromAddress { get; private set; } = string.Empty;
    public string ToAddress { get; private set; } = string.Empty;
    public string Subject { get; private set; } = string.Empty;
    public string Body { get; private set; } = string.Empty;
    public string? InternetMessageId { get; private set; }
    public DateTimeOffset CreatedUtc { get; private set; }
}
