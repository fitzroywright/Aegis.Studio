namespace Aegis.Studio.Domain.Communications;

public sealed class Conversation
{
    private Conversation()
    {
    }

    public Conversation(Guid workspaceId, Guid? bookingId, string clientEmail, string subject)
    {
        Id = Guid.NewGuid();
        WorkspaceId = workspaceId;
        BookingId = bookingId;
        ClientEmail = clientEmail.Trim().ToLowerInvariant();
        Subject = subject.Trim();
        Status = ConversationStatus.New;
        CreatedUtc = DateTimeOffset.UtcNow;
        LastActivityUtc = CreatedUtc;
    }

    public Guid Id { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public Guid? BookingId { get; private set; }
    public string ClientEmail { get; private set; } = string.Empty;
    public string Subject { get; private set; } = string.Empty;
    public ConversationStatus Status { get; private set; }
    public string? AssignedUserId { get; private set; }
    public DateTimeOffset CreatedUtc { get; private set; }
    public DateTimeOffset LastActivityUtc { get; private set; }

    public void Touch()
    {
        LastActivityUtc = DateTimeOffset.UtcNow;
        if (Status == ConversationStatus.New)
        {
            Status = ConversationStatus.Open;
        }
    }
}
