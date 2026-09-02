namespace Aegis.Studio.Domain.Tasks;

public sealed class StudioTask
{
    private StudioTask()
    {
    }

    public StudioTask(Guid workspaceId, Guid? bookingId, Guid? conversationId, string title, string description, string? assignedUserId, DateTimeOffset? dueUtc)
    {
        Id = Guid.NewGuid();
        WorkspaceId = workspaceId;
        BookingId = bookingId;
        ConversationId = conversationId;
        Title = title.Trim();
        Description = description.Trim();
        AssignedUserId = string.IsNullOrWhiteSpace(assignedUserId) ? null : assignedUserId.Trim();
        DueUtc = dueUtc;
        Status = StudioTaskStatus.Open;
        CreatedUtc = DateTimeOffset.UtcNow;
        UpdatedUtc = CreatedUtc;
    }

    public Guid Id { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public Guid? BookingId { get; private set; }
    public Guid? ConversationId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string? AssignedUserId { get; private set; }
    public DateTimeOffset? DueUtc { get; private set; }
    public StudioTaskStatus Status { get; private set; }
    public DateTimeOffset CreatedUtc { get; private set; }
    public DateTimeOffset UpdatedUtc { get; private set; }
    public DateTimeOffset? CompletedUtc { get; private set; }

    public void Start()
    {
        Status = StudioTaskStatus.InProgress;
        UpdatedUtc = DateTimeOffset.UtcNow;
    }

    public void Complete()
    {
        Status = StudioTaskStatus.Completed;
        CompletedUtc = DateTimeOffset.UtcNow;
        UpdatedUtc = CompletedUtc.Value;
    }

    public void AssignTo(string? userId)
    {
        AssignedUserId = string.IsNullOrWhiteSpace(userId) ? null : userId.Trim();
        UpdatedUtc = DateTimeOffset.UtcNow;
    }
}
