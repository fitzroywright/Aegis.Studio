namespace Aegis.Studio.Domain.Notifications;

public sealed class NotificationJob
{
    private NotificationJob()
    {
    }

    public NotificationJob(Guid workspaceId, Guid? bookingId, string channel, string recipient, string subject, string body, DateTimeOffset dueUtc)
    {
        Id = Guid.NewGuid();
        WorkspaceId = workspaceId;
        BookingId = bookingId;
        Channel = channel.Trim();
        Recipient = recipient.Trim();
        Subject = subject.Trim();
        Body = body;
        DueUtc = dueUtc;
        Status = NotificationStatus.Pending;
        CreatedUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public Guid? BookingId { get; private set; }
    public string Channel { get; private set; } = string.Empty;
    public string Recipient { get; private set; } = string.Empty;
    public string Subject { get; private set; } = string.Empty;
    public string Body { get; private set; } = string.Empty;
    public DateTimeOffset DueUtc { get; private set; }
    public NotificationStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset CreatedUtc { get; private set; }
    public DateTimeOffset? DeliveredUtc { get; private set; }

    public void MarkDelivered()
    {
        Attempts++;
        Status = NotificationStatus.Delivered;
        DeliveredUtc = DateTimeOffset.UtcNow;
        LastError = null;
    }

    public void MarkFailed(string error)
    {
        Attempts++;
        Status = NotificationStatus.Failed;
        LastError = error;
    }

    public void Retry(DateTimeOffset dueUtc)
    {
        DueUtc = dueUtc;
        Status = NotificationStatus.Pending;
    }
}
