using System.Collections.Concurrent;
using Aegis.Pos.Core;
using Aegis.Studio.Web.Demo;

namespace Aegis.Studio.Web.Pos;

public sealed class StudioPosStore
{
    private readonly ConcurrentDictionary<Guid, Sale> sales = new();
    private readonly ConcurrentDictionary<Guid, byte> postedFinancialEvents = new();

    public IReadOnlyList<Sale> GetForJob(Guid jobId)
        => sales.Values.Where(x => x.ReferenceType == "StudioJob" && x.ReferenceId == jobId.ToString("D"))
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToList();

    public Sale? Get(Guid saleId)
    {
        sales.TryGetValue(saleId, out Sale? sale);
        return sale;
    }

    public Sale Create(Guid jobId, CreateStudioSaleRequest request, DemoStore studio)
    {
        if (studio.GetJob(jobId) is null)
        {
            throw new KeyNotFoundException("Job was not found.");
        }
        if (request.Lines is null || request.Lines.Count == 0)
        {
            throw new InvalidOperationException("At least one sale line is required.");
        }

        Sale sale = new(Guid.NewGuid(), "StudioJob", jobId.ToString("D"), DateTimeOffset.UtcNow);
        foreach (CreateStudioSaleLineRequest line in request.Lines)
        {
            sale.AddLine(SaleLine.Create(line.Code ?? string.Empty, line.Description, line.UnitPrice, line.Quantity));
        }
        sales[sale.Id] = sale;
        return sale;
    }

    public Sale Complete(Guid saleId, CompleteStudioSaleRequest request, DemoStore studio, string cashierUserId)
    {
        Sale sale = sales[saleId];
        if (sale.Status == SaleStatus.Completed)
        {
            return sale;
        }

        Tender[] tenders = request.Tenders.Select(x => Tender.Create(x.Kind, x.Amount, x.Reference)).ToArray();
        sale.Submit(DateTimeOffset.UtcNow);
        sale.Complete(tenders, DateTimeOffset.UtcNow, cashierUserId, request.TerminalId ?? "StudioWeb");

        if (postedFinancialEvents.TryAdd(sale.Id, 0))
        {
            Guid jobId = Guid.Parse(sale.ReferenceId);
            string tenderSummary = string.Join(", ", sale.Tenders.Select(x => $"{x.Kind} {x.Amount:N2}"));
            studio.AddFinancialEvent(jobId, new AddFinancialEventRequest("POS Sale", sale.Total, $"Sale {sale.Id}: {tenderSummary}"));
        }

        return sale;
    }

    public Sale Void(Guid saleId, string userId, string reason)
    {
        Sale sale = sales[saleId];
        sale.Void(userId, DateTimeOffset.UtcNow, reason);
        return sale;
    }

    public Sale Refund(Guid saleId, RefundStudioSaleRequest request, DemoStore studio)
    {
        Sale sale = sales[saleId];
        RefundLine[] lines = request.Lines.Select(x => new RefundLine(x.SaleLineId, x.UnitPrice, x.Quantity)).ToArray();
        Tender[] tenders = request.Tenders.Select(x => Tender.Create(x.Kind, x.Amount, x.Reference)).ToArray();
        Refund refund = new(Guid.NewGuid(), sale.Id, lines, tenders, request.Reason, DateTimeOffset.UtcNow);
        sale.RecordRefund(refund);

        Guid jobId = Guid.Parse(sale.ReferenceId);
        studio.AddFinancialEvent(jobId, new AddFinancialEventRequest("POS Refund", -refund.Total, $"Refund {refund.Id}: {refund.Reason}"));
        return sale;
    }
}

public sealed record CreateStudioSaleRequest(IReadOnlyList<CreateStudioSaleLineRequest> Lines);
public sealed record CreateStudioSaleLineRequest(string? Code, string Description, decimal UnitPrice, int Quantity);
public sealed record CompleteStudioSaleRequest(IReadOnlyList<StudioTenderRequest> Tenders, string? TerminalId);
public sealed record StudioTenderRequest(TenderKind Kind, decimal Amount, string? Reference);
public sealed record VoidStudioSaleRequest(string Reason);
public sealed record RefundStudioSaleRequest(IReadOnlyList<RefundStudioSaleLineRequest> Lines, IReadOnlyList<StudioTenderRequest> Tenders, string Reason);
public sealed record RefundStudioSaleLineRequest(Guid SaleLineId, decimal UnitPrice, int Quantity);
