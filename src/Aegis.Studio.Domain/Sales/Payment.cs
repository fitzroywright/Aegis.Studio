namespace Aegis.Studio.Domain.Sales;

public sealed class Payment
{
    private Payment()
    {
    }

    internal Payment(Guid saleId, Guid jobId, decimal amount, PaymentMethod method, string reference, bool isRefund)
    {
        if (amount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        Id = Guid.NewGuid();
        SaleId = saleId;
        JobId = jobId;
        Amount = amount;
        Method = method;
        Reference = reference.Trim();
        IsRefund = isRefund;
        CreatedUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }
    public Guid SaleId { get; private set; }
    public Guid JobId { get; private set; }
    public decimal Amount { get; private set; }
    public PaymentMethod Method { get; private set; }
    public string Reference { get; private set; } = string.Empty;
    public bool IsRefund { get; private set; }
    public DateTimeOffset CreatedUtc { get; private set; }
}
