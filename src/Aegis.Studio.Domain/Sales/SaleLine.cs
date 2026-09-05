namespace Aegis.Studio.Domain.Sales;

public sealed class SaleLine
{
    internal SaleLine(Guid saleId, string description, decimal quantity, decimal unitPrice, string accountingCode)
    {
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity));
        }

        if (unitPrice < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(unitPrice));
        }

        Id = Guid.NewGuid();
        SaleId = saleId;
        Description = description.Trim();
        Quantity = quantity;
        UnitPrice = unitPrice;
        AccountingCode = accountingCode.Trim();
    }

    public Guid Id { get; private set; }
    public Guid SaleId { get; private set; }
    public string Description { get; private set; } = string.Empty;
    public decimal Quantity { get; private set; }
    public decimal UnitPrice { get; private set; }
    public string AccountingCode { get; private set; } = string.Empty;
    public decimal LineTotal => Quantity * UnitPrice;
}
