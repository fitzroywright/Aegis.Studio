namespace Aegis.Pos.Core;

public sealed record SaleLine(Guid Id, string Code, string Description, decimal UnitPrice, int Quantity)
{
    public decimal Total => UnitPrice * Quantity;

    public static SaleLine Create(string code, string description, decimal unitPrice, int quantity)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("Description is required.", nameof(description));
        }
        if (unitPrice < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(unitPrice));
        }
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity));
        }

        return new SaleLine(Guid.NewGuid(), code?.Trim() ?? string.Empty, description.Trim(), unitPrice, quantity);
    }
}
