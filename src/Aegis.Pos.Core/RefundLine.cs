namespace Aegis.Pos.Core;

public sealed record RefundLine(Guid SaleLineId, decimal UnitPrice, int Quantity)
{
    public decimal Total => UnitPrice * Quantity;
}
