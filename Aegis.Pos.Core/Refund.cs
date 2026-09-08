namespace Aegis.Pos.Core;

public sealed class Refund
{
    public Refund(Guid id, Guid saleId, IEnumerable<RefundLine> lines, IEnumerable<Tender> tenders, string reason, DateTimeOffset createdAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Refund ID is required.", nameof(id));
        }
        if (saleId == Guid.Empty)
        {
            throw new ArgumentException("Sale ID is required.", nameof(saleId));
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Refund reason is required.", nameof(reason));
        }

        RefundLine[] refundLines = lines?.ToArray() ?? throw new ArgumentNullException(nameof(lines));
        Tender[] refundTenders = tenders?.ToArray() ?? throw new ArgumentNullException(nameof(tenders));
        if (refundLines.Length == 0 || refundTenders.Length == 0)
        {
            throw new InvalidOperationException("A refund requires lines and tender allocations.");
        }

        Id = id;
        SaleId = saleId;
        Lines = refundLines;
        Tenders = refundTenders;
        Reason = reason.Trim();
        CreatedAtUtc = createdAtUtc.ToUniversalTime();

        if (Tenders.Sum(x => x.Amount) != Total)
        {
            throw new InvalidOperationException("Refund tender allocations must equal the refund total.");
        }
    }

    public Guid Id { get; }
    public Guid SaleId { get; }
    public IReadOnlyCollection<RefundLine> Lines { get; }
    public IReadOnlyCollection<Tender> Tenders { get; }
    public string Reason { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public decimal Total => Lines.Sum(x => x.Total);
}
