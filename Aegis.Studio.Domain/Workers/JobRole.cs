namespace Aegis.Studio.Domain.Workers;

public sealed record JobRole(
    string Code,
    string DisplayName,
    bool CanBePrimary = true,
    bool IsActive = true)
{
    public string NormalizedCode => Code.Trim().ToUpperInvariant();
}
