namespace Aegis.Studio.Domain.Operations;

public sealed class OperationalAlert
{
    private OperationalAlert()
    {
    }

    public OperationalAlert(Guid workspaceId, Guid? conversationId, Guid? bookingId, string kind, string message, int severity)
    {
        Id = Guid.NewGuid();
        WorkspaceId = workspaceId;
        ConversationId = conversationId;
        BookingId = bookingId;
        Kind = kind.Trim();
        Message = message.Trim();
        Severity = severity;
        CreatedUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public Guid? ConversationId { get; private set; }
    public Guid? BookingId { get; private set; }
    public string Kind { get; private set; } = string.Empty;
    public string Message { get; private set; } = string.Empty;
    public int Severity { get; private set; }
    public bool IsAcknowledged { get; private set; }
    public DateTimeOffset CreatedUtc { get; private set; }

    public void Acknowledge()
    {
        IsAcknowledged = true;
    }
}
