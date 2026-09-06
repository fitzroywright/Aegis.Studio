namespace Aegis.Pos.Core;

public sealed class Sale
{
    private readonly List<SaleLine> lines = [];
    private readonly List<Tender> tenders = [];
    private readonly List<Refund> refunds = [];
    private readonly PosEngine engine = new();

    public Sale(Guid id, string referenceType, string referenceId, DateTimeOffset createdAtUtc)
    {
        if (id == Guid.Empty) throw new ArgumentException("Sale ID is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(referenceType)) throw new ArgumentException("Reference type is required.", nameof(referenceType));
        if (string.IsNullOrWhiteSpace(referenceId)) throw new ArgumentException("Reference ID is required.", nameof(referenceId));

        Id = id;
        ReferenceType = referenceType.Trim();
        ReferenceId = referenceId.Trim();
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        Status = SaleStatus.Draft;
    }

    public Guid Id { get; }
    public string ReferenceType { get; }
    public string ReferenceId { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset? SubmittedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public DateTimeOffset? VoidedAtUtc { get; private set; }
    public string? VoidedBy { get; private set; }
    public string? VoidReason { get; private set; }
    public string? CashierUserId { get; private set; }
    public string? TerminalId { get; private set; }
    public SaleStatus Status { get; private set; }
    public IReadOnlyCollection<SaleLine> Lines => lines.AsReadOnly();
    public IReadOnlyCollection<Tender> Tenders => tenders.AsReadOnly();
    public IReadOnlyCollection<Refund> Refunds => refunds.AsReadOnly();
    public decimal Total => engine.CalculateTotal(lines);
    public decimal AmountPaid => tenders.Sum(x => x.Amount);
    public decimal RefundedAmount => refunds.Sum(x => x.Total);
    public decimal NetTotal => Total - RefundedAmount;

    public void AddLine(SaleLine line)
    {
        EnsureDraft();
        ArgumentNullException.ThrowIfNull(line);
        lines.Add(line);
    }

    public void RemoveLine(Guid lineId)
    {
        EnsureDraft();
        SaleLine line = lines.SingleOrDefault(x => x.Id == lineId)
            ?? throw new InvalidOperationException("The sale line was not found.");
        lines.Remove(line);
    }

    public void Submit(DateTimeOffset submittedAtUtc)
    {
        EnsureDraft();
        if (lines.Count == 0) throw new InvalidOperationException("An empty sale cannot be submitted.");
        Status = SaleStatus.Submitted;
        SubmittedAtUtc = submittedAtUtc.ToUniversalTime();
    }

    public void Complete(IEnumerable<Tender> paymentTenders, DateTimeOffset completedAtUtc, string cashierUserId, string terminalId)
    {
        if (Status != SaleStatus.Submitted) throw new InvalidOperationException("Only a submitted sale can be completed.");
        if (string.IsNullOrWhiteSpace(cashierUserId)) throw new ArgumentException("Cashier user ID is required.", nameof(cashierUserId));
        if (string.IsNullOrWhiteSpace(terminalId)) throw new ArgumentException("Terminal ID is required.", nameof(terminalId));

        Tender[] values = paymentTenders?.ToArray() ?? throw new ArgumentNullException(nameof(paymentTenders));
        engine.EnsureTenderTotal(Total, values);
        tenders.AddRange(values);
        CashierUserId = cashierUserId.Trim();
        TerminalId = terminalId.Trim();
        CompletedAtUtc = completedAtUtc.ToUniversalTime();
        Status = SaleStatus.Completed;
    }

    public void Void(string voidedBy, DateTimeOffset voidedAtUtc, string reason)
    {
        if (Status is SaleStatus.Completed or SaleStatus.Voided) throw new InvalidOperationException("A completed or already voided sale cannot be voided.");
        if (string.IsNullOrWhiteSpace(voidedBy)) throw new ArgumentException("Voiding user is required.", nameof(voidedBy));
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A void reason is required.", nameof(reason));

        VoidedBy = voidedBy.Trim();
        VoidReason = reason.Trim();
        VoidedAtUtc = voidedAtUtc.ToUniversalTime();
        Status = SaleStatus.Voided;
    }

    public void RecordRefund(Refund refund)
    {
        engine.EnsureRefundAllowed(this, refund);
        refunds.Add(refund);
    }

    private void EnsureDraft()
    {
        if (Status != SaleStatus.Draft) throw new InvalidOperationException("Only a draft sale can be modified.");
    }
}
