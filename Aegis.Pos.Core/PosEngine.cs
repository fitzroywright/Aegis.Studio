namespace Aegis.Pos.Core;

public sealed class PosEngine
{
    public decimal CalculateTotal(IEnumerable<SaleLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return lines.Sum(x => x.Total);
    }

    public void EnsureTenderTotal(decimal saleTotal, IEnumerable<Tender> tenders)
    {
        ArgumentNullException.ThrowIfNull(tenders);
        Tender[] values = tenders.ToArray();
        if (values.Length == 0)
        {
            throw new InvalidOperationException("A completed sale requires at least one tender.");
        }
        if (values.Any(x => x.Amount <= 0m))
        {
            throw new InvalidOperationException("Tender amounts must be positive.");
        }
        if (values.Sum(x => x.Amount) != saleTotal)
        {
            throw new InvalidOperationException("Tender amounts must equal the sale total.");
        }
    }

    public void EnsureRefundAllowed(Sale sale, Refund refund)
    {
        ArgumentNullException.ThrowIfNull(sale);
        ArgumentNullException.ThrowIfNull(refund);
        if (sale.Status != SaleStatus.Completed)
        {
            throw new InvalidOperationException("Only a completed sale can be refunded.");
        }
        if (refund.SaleId != sale.Id)
        {
            throw new InvalidOperationException("The refund does not belong to this sale.");
        }
        if (sale.RefundedAmount + refund.Total > sale.Total)
        {
            throw new InvalidOperationException("Refunds cannot exceed the original sale total.");
        }

        foreach (RefundLine refundLine in refund.Lines)
        {
            SaleLine original = sale.Lines.SingleOrDefault(x => x.Id == refundLine.SaleLineId)
                ?? throw new InvalidOperationException("A refunded line does not exist on the original sale.");
            if (original.UnitPrice != refundLine.UnitPrice)
            {
                throw new InvalidOperationException("Refund unit price must match the original sale line.");
            }
            int alreadyRefunded = sale.Refunds.SelectMany(x => x.Lines)
                .Where(x => x.SaleLineId == refundLine.SaleLineId)
                .Sum(x => x.Quantity);
            if (refundLine.Quantity <= 0 || refundLine.Quantity > original.Quantity - alreadyRefunded)
            {
                throw new InvalidOperationException("Refund quantity exceeds the remaining refundable quantity.");
            }
        }

        foreach (IGrouping<TenderKind, Tender> tenderGroup in refund.Tenders.GroupBy(x => x.Kind))
        {
            decimal paid = sale.Tenders.Where(x => x.Kind == tenderGroup.Key).Sum(x => x.Amount);
            decimal alreadyRefunded = sale.Refunds.SelectMany(x => x.Tenders).Where(x => x.Kind == tenderGroup.Key).Sum(x => x.Amount);
            if (tenderGroup.Sum(x => x.Amount) > paid - alreadyRefunded)
            {
                throw new InvalidOperationException($"Refund allocation exceeds remaining {tenderGroup.Key} tender.");
            }
        }
    }
}
