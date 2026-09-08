using Aegis.Studio.Domain.Common;

namespace Aegis.Studio.Domain.Jobs;

public sealed class Job : Entity
{
    public Job(Guid id, Guid workspaceId, Guid contactId, string title, DateTimeOffset createdAt) : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        WorkspaceId = workspaceId;
        ContactId = contactId;
        Title = title.Trim();
        CreatedAt = createdAt;
        Status = JobStatus.New;
    }

    public Guid WorkspaceId { get; }

    public Guid ContactId { get; }

    public string Title { get; }

    public DateTimeOffset CreatedAt { get; }

    public JobStatus Status { get; private set; }

    public void MoveTo(JobStatus status)
    {
        if (Status is JobStatus.Closed or JobStatus.Cancelled)
        {
            throw new InvalidOperationException("Closed or cancelled jobs cannot be moved to another workflow stage.");
        }

        Status = status;
    }
}
