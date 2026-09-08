using Aegis.Pos.Core;
using Xunit;

namespace Aegis.Studio.Domain.Tests;

public sealed class PosCoreTests
{
    [Fact]
    public void Sale_StoresUtcAndSupportsSplitTender()
    {
        Sale sale = new(Guid.NewGuid(), "StudioJob", Guid.NewGuid().ToString("D"), DateTimeOffset.Now);
        sale.AddLine(SaleLine.Create("DEPOSIT", "Wedding Deposit", 25000m, 1));
        sale.Submit(DateTimeOffset.Now);
        sale.Complete(
        [
            Tender.Create(TenderKind.Cash, 10000m),
            Tender.Create(TenderKind.Card, 15000m, "POS-123")
        ],
        DateTimeOffset.Now,
        "staff",
        "StudioWeb");

        Assert.Equal(SaleStatus.Completed, sale.Status);
        Assert.Equal(25000m, sale.AmountPaid);
        Assert.Equal(TimeSpan.Zero, sale.CreatedAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, sale.CompletedAtUtc!.Value.Offset);
    }

    [Fact]
    public void Refund_CannotExceedRemainingQuantity()
    {
        Sale sale = new(Guid.NewGuid(), "StudioJob", Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow);
        SaleLine line = SaleLine.Create("PRINT", "Print", 1000m, 2);
        sale.AddLine(line);
        sale.Submit(DateTimeOffset.UtcNow);
        sale.Complete([Tender.Create(TenderKind.Cash, 2000m)], DateTimeOffset.UtcNow, "staff", "StudioWeb");
        sale.RecordRefund(new Refund(Guid.NewGuid(), sale.Id, [new RefundLine(line.Id, 1000m, 1)], [Tender.Create(TenderKind.Cash, 1000m)], "First print", DateTimeOffset.UtcNow));

        Assert.Throws<InvalidOperationException>(() => sale.RecordRefund(new Refund(
            Guid.NewGuid(), sale.Id,
            [new RefundLine(line.Id, 1000m, 2)],
            [Tender.Create(TenderKind.Cash, 2000m)],
            "Too many",
            DateTimeOffset.UtcNow)));
    }
}
