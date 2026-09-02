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
        IsUnread = true;
        CreatedUtc = DateTimeOffset.UtcNow;
        LastActivityUtc = CreatedUtc;
    }

    public Guid Id { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public Guid? BookingId { get; private set; }
    public string ClientEmail { get; private set; } = string.Empty;
    public string Subject { get; private set; } = string.Empty;
    public ConversationStatus Status { get; private set; }
    public bool IsUnread { get; private set; }
    public string? AssignedUserId { get; private set; }
    public DateTimeOffset CreatedUtc { get; private set; }
    public DateTimeOffset LastActivityUtc { get; private set; }

    public void ReceiveInbound()
    {
        LastActivityUtc = DateTimeOffset.UtcNow;
        IsUnread = true;
        if (Status == ConversationStatus.New || Status == ConversationStatus.WaitingForCustomer)
        {
            Status = ConversationStatus.Open;
        }
    }

    public void MarkRead()
    {
        IsUnread = false;
        if (Status == ConversationStatus.New)
        {
            Status = ConversationStatus.Open;
        }
    }

    public void MarkWaitingForCustomer()
    {
        LastActivityUtc = DateTimeOffset.UtcNow;
        IsUnread = false;
        Status = ConversationStatus.WaitingForCustomer;
    }

    public void Resolve()
    {
        LastActivityUtc = DateTimeOffset.UtcNow;
        IsUnread = false;
        Status = ConversationStatus.Resolved;
    }

    public void Close()
    {
        LastActivityUtc = DateTimeOffset.UtcNow;
        IsUnread = false;
        Status = ConversationStatus.Closed;
    }

    public void Reopen()
    {
        LastActivityUtc = DateTimeOffset.UtcNow;
        Status = ConversationStatus.Open;
    }

    public void AssignTo(string? userId)
    {
        AssignedUserId = string.IsNullOrWhiteSpace(userId) ? null : userId.Trim();
        LastActivityUtc = DateTimeOffset.UtcNow;
    }

    public void Touch()
    {
        LastActivityUtc = DateTimeOffset.UtcNow;
    }
}
