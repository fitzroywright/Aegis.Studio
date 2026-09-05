namespace Aegis.Studio.Domain.Jobs;

public sealed class JobActivity
{
    private JobActivity()
    {
    }

    public JobActivity(Guid jobId, JobActivityKind kind, string summary, string sourceReference, string createdBy)
    {
        Id = Guid.NewGuid();
        JobId = jobId;
        Kind = kind;
        Summary = summary.Trim();
        SourceReference = sourceReference.Trim();
        CreatedBy = createdBy.Trim();
        CreatedUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }
    public Guid JobId { get; private set; }
    public JobActivityKind Kind { get; private set; }
    public string Summary { get; private set; } = string.Empty;
    public string SourceReference { get; private set; } = string.Empty;
    public string CreatedBy { get; private set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; private set; }
}
