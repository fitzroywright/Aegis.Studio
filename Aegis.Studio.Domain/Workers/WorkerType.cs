namespace Aegis.Studio.Domain.Workers;

public sealed record WorkerType(
    string Code,
    string DisplayName,
    bool CanBePrimary = true)
{
    public string NormalizedCode => Code.Trim().ToUpperInvariant();
}
