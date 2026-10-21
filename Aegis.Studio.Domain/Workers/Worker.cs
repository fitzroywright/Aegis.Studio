namespace Aegis.Studio.Domain.Workers;

public sealed record Worker(
    string UserName,
    string DisplayName,
    string WorkerTypeCode,
    bool IsActive,
    string? Email = null,
    string? Phone = null)
{
    public string NormalizedWorkerTypeCode => WorkerTypeCode.Trim().ToUpperInvariant();
}
