namespace Aegis.Studio.Domain.Workers;

public sealed record Worker(
    string UserName,
    string DisplayName,
    string JobRoleCode,
    bool IsActive,
    string? Email = null,
    string? Phone = null)
{
    public string NormalizedJobRoleCode => JobRoleCode.Trim().ToUpperInvariant();
}
