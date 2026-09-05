namespace Aegis.Studio.Domain.Jobs;

public sealed class StudioJob
{
    private StudioJob()
    {
    }

    public StudioJob(Guid workspaceId, string title, Guid? customerId, string createdBy)
    {
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace is required.", nameof(workspaceId));
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Job title is required.", nameof(title));
        }

        Id = Guid.NewGuid();
        WorkspaceId = workspaceId;
        CustomerId = customerId;
        Title = title.Trim();
        Status = StudioJobStatus.Open;
        CreatedBy = createdBy.Trim();
        CreatedUtc = DateTimeOffset.UtcNow;
        UpdatedUtc = CreatedUtc;
    }

    public Guid Id { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public Guid? CustomerId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public StudioJobStatus Status { get; private set; }
    public string CreatedBy { get; private set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; private set; }
    public DateTimeOffset UpdatedUtc { get; private set; }

    public void AssignCustomer(Guid customerId)
    {
        if (customerId == Guid.Empty)
        {
            throw new ArgumentException("Customer is required.", nameof(customerId));
        }

        CustomerId = customerId;
        UpdatedUtc = DateTimeOffset.UtcNow;
    }

    public void Close()
    {
        Status = StudioJobStatus.Closed;
        UpdatedUtc = DateTimeOffset.UtcNow;
    }

    public void Reopen()
    {
        Status = StudioJobStatus.Open;
        UpdatedUtc = DateTimeOffset.UtcNow;
    }
}
