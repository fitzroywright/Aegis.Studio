namespace Aegis.Studio.Domain.Workers;

public sealed record WorkerAssignment(
    Guid Id,
    Guid JobId,
    string WorkerUserName,
    string JobRoleCode,
    string Responsibility,
    bool IsPrimary,
    string Status,
    DateTimeOffset? StartsAtUtc = null,
    DateTimeOffset? EndsAtUtc = null,
    DateTimeOffset? AcceptedAtUtc = null,
    DateTimeOffset? CompletedAtUtc = null)
{
    public string NormalizedJobRoleCode => JobRoleCode.Trim().ToUpperInvariant();

    public bool IsActive =>
        !string.Equals(Status, "Cancelled", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(Status, "Completed", StringComparison.OrdinalIgnoreCase);
}
