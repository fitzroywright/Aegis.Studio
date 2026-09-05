namespace Aegis.Studio.Domain.Sales;

public sealed class Sale
{
    private readonly List<SaleLine> lines = new();
    private readonly List<Payment> payments = new();

    private Sale()
    {
    }

    public Sale(Guid jobId, Guid customerId, string cashierUserName)
    {
        Id = Guid.NewGuid();
        JobId = jobId;
        CustomerId = customerId;
        CashierUserName = cashierUserName.Trim();
        Status = SaleStatus.Open;
        CreatedUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }
    public Guid JobId { get; private set; }
    public Guid CustomerId { get; private set; }
    public string CashierUserName { get; private set; } = string.Empty;
    public SaleStatus Status { get; private set; }
    public DateTimeOffset CreatedUtc { get; private set; }
    public IReadOnlyCollection<SaleLine> Lines => lines.AsReadOnly();
    public IReadOnlyCollection<Payment> Payments => payments.AsReadOnly();
    public decimal Total => lines.Sum(line => line.LineTotal);
    public decimal AmountPaid => payments.Where(payment => !payment.IsRefund).Sum(payment => payment.Amount) - payments.Where(payment => payment.IsRefund).Sum(payment => payment.Amount);
    public decimal Balance => Total - AmountPaid;

    public void AddLine(string description, decimal quantity, decimal unitPrice, string accountingCode)
    {
        EnsureOpen();
        lines.Add(new SaleLine(Id, description, quantity, unitPrice, accountingCode));
    }

    public Payment TakePayment(decimal amount, PaymentMethod method, string reference)
    {
        EnsureOpen();
        Payment payment = new(Id, JobId, amount, method, reference, false);
        payments.Add(payment);
        return payment;
    }

    public Payment Refund(decimal amount, PaymentMethod method, string reference)
    {
        if (amount <= 0 || amount > AmountPaid)
        {
            throw new InvalidOperationException("Refund must be positive and cannot exceed net payments received.");
        }

        Payment payment = new(Id, JobId, amount, method, reference, true);
        payments.Add(payment);
        return payment;
    }

    public void Complete()
    {
        if (lines.Count == 0)
        {
            throw new InvalidOperationException("A sale must contain at least one line.");
        }

        Status = SaleStatus.Completed;
    }

    private void EnsureOpen()
    {
        if (Status != SaleStatus.Open)
        {
            throw new InvalidOperationException("Only an open sale can be modified.");
        }
    }
}
