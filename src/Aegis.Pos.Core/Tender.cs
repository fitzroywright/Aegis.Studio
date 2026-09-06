namespace Aegis.Pos.Core;

public sealed record Tender(Guid Id, TenderKind Kind, decimal Amount, string? Reference = null)
{
    public static Tender Create(TenderKind kind, decimal amount, string? reference = null)
    {
        if (amount <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        return new Tender(Guid.NewGuid(), kind, amount, string.IsNullOrWhiteSpace(reference) ? null : reference.Trim());
    }
}
