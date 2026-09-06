using Aegis.Pos.Core;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;

namespace Aegis.Studio.Web.Pos;

public sealed class StudioPosStore
{
    private const string SalesCollection = "pos-sales";
    private readonly PostgresDocumentStore persistence;

    public StudioPosStore(PostgresDocumentStore persistence)
    {
        this.persistence = persistence;
    }

    public IReadOnlyList<Sale> GetForJob(Guid jobId)
        => persistence.GetAll<PersistedSale>(SalesCollection)
            .Where(x => x.ReferenceType == "StudioJob" && x.ReferenceId == jobId.ToString("D"))
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(Rehydrate)
            .ToList();

    public Sale? Get(Guid saleId)
    {
        PersistedSale? snapshot = persistence.Get<PersistedSale>(SalesCollection, saleId);
        return snapshot is null ? null : Rehydrate(snapshot);
    }

    public Sale Create(Guid jobId, CreateStudioSaleRequest request, StudioStore studio)
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
        Save(sale, false);
        return sale;
    }

    public Sale Complete(Guid saleId, CompleteStudioSaleRequest request, StudioStore studio, string cashierUserId)
    {
        PersistedSale snapshot = persistence.Get<PersistedSale>(SalesCollection, saleId) ?? throw new KeyNotFoundException("Sale was not found.");
        Sale sale = Rehydrate(snapshot);
        if (sale.Status == SaleStatus.Completed)
        {
            EnsureFinancialEventPosted(sale, snapshot, studio);
            return sale;
        }
        if (sale.Status == SaleStatus.Voided)
        {
            throw new InvalidOperationException("A voided sale cannot be completed.");
        }

        Tender[] tenders = request.Tenders.Select(x => Tender.Create(x.Kind, x.Amount, x.Reference)).ToArray();
        if (sale.Status == SaleStatus.Draft)
        {
            sale.Submit(DateTimeOffset.UtcNow);
        }
        sale.Complete(tenders, DateTimeOffset.UtcNow, cashierUserId, request.TerminalId ?? "StudioWeb");
        snapshot = Save(sale, snapshot.FinancialEventPosted);
        EnsureFinancialEventPosted(sale, snapshot, studio);
        return sale;
    }

    public Sale Void(Guid saleId, string userId, string reason)
    {
        PersistedSale snapshot = persistence.Get<PersistedSale>(SalesCollection, saleId) ?? throw new KeyNotFoundException("Sale was not found.");
        Sale sale = Rehydrate(snapshot);
        sale.Void(userId, DateTimeOffset.UtcNow, reason);
        Save(sale, snapshot.FinancialEventPosted);
        return sale;
    }

    public Sale Refund(Guid saleId, RefundStudioSaleRequest request, StudioStore studio)
    {
        PersistedSale snapshot = persistence.Get<PersistedSale>(SalesCollection, saleId) ?? throw new KeyNotFoundException("Sale was not found.");
        Sale sale = Rehydrate(snapshot);
        RefundLine[] lines = request.Lines.Select(x => new RefundLine(x.SaleLineId, x.UnitPrice, x.Quantity)).ToArray();
        Tender[] tenders = request.Tenders.Select(x => Tender.Create(x.Kind, x.Amount, x.Reference)).ToArray();
        Refund refund = new(Guid.NewGuid(), sale.Id, lines, tenders, request.Reason, DateTimeOffset.UtcNow);
        sale.RecordRefund(refund);
        Save(sale, snapshot.FinancialEventPosted);

        Guid jobId = Guid.Parse(sale.ReferenceId);
        studio.AddFinancialEvent(jobId, new AddFinancialEventRequest("POS Refund", -refund.Total, $"Refund {refund.Id}: {refund.Reason}"));
        return sale;
    }

    private void EnsureFinancialEventPosted(Sale sale, PersistedSale snapshot, StudioStore studio)
    {
        if (snapshot.FinancialEventPosted)
        {
            return;
        }

        Guid jobId = Guid.Parse(sale.ReferenceId);
        string tenderSummary = string.Join(", ", sale.Tenders.Select(x => $"{x.Kind} {x.Amount:N2}"));
        studio.AddFinancialEvent(jobId, new AddFinancialEventRequest("POS Sale", sale.Total, $"Sale {sale.Id}: {tenderSummary}"));
        Save(sale, true);
    }

    private PersistedSale Save(Sale sale, bool financialEventPosted)
    {
        PersistedSale snapshot = new(
            sale.Id,
            sale.ReferenceType,
            sale.ReferenceId,
            sale.CreatedAtUtc,
            sale.SubmittedAtUtc,
            sale.CompletedAtUtc,
            sale.VoidedAtUtc,
            sale.VoidedBy,
            sale.VoidReason,
            sale.CashierUserId,
            sale.TerminalId,
            sale.Status,
            sale.Lines.ToList(),
            sale.Tenders.ToList(),
            sale.Refunds.Select(x => new PersistedRefund(x.Id, x.Lines.ToList(), x.Tenders.ToList(), x.Reason, x.CreatedAtUtc)).ToList(),
            financialEventPosted);
        persistence.Upsert(SalesCollection, sale.Id, snapshot);
        return snapshot;
    }

    private static Sale Rehydrate(PersistedSale snapshot)
    {
        Sale sale = new(snapshot.Id, snapshot.ReferenceType, snapshot.ReferenceId, snapshot.CreatedAtUtc);
        foreach (SaleLine line in snapshot.Lines)
        {
            sale.AddLine(line);
        }

        if (snapshot.Status != SaleStatus.Draft)
        {
            sale.Submit(snapshot.SubmittedAtUtc ?? snapshot.CreatedAtUtc);
        }
        if (snapshot.Status == SaleStatus.Completed)
        {
            sale.Complete(snapshot.Tenders, snapshot.CompletedAtUtc ?? snapshot.CreatedAtUtc, snapshot.CashierUserId ?? "Aegis.Studio", snapshot.TerminalId ?? "StudioWeb");
            foreach (PersistedRefund persistedRefund in snapshot.Refunds)
            {
                sale.RecordRefund(new Refund(persistedRefund.Id, sale.Id, persistedRefund.Lines, persistedRefund.Tenders, persistedRefund.Reason, persistedRefund.CreatedAtUtc));
            }
        }
        else if (snapshot.Status == SaleStatus.Voided)
        {
            sale.Void(snapshot.VoidedBy ?? "Aegis.Studio", snapshot.VoidedAtUtc ?? snapshot.CreatedAtUtc, snapshot.VoidReason ?? "Voided");
        }
        return sale;
    }
}

public sealed record PersistedSale(Guid Id, string ReferenceType, string ReferenceId, DateTimeOffset CreatedAtUtc, DateTimeOffset? SubmittedAtUtc, DateTimeOffset? CompletedAtUtc, DateTimeOffset? VoidedAtUtc, string? VoidedBy, string? VoidReason, string? CashierUserId, string? TerminalId, SaleStatus Status, List<SaleLine> Lines, List<Tender> Tenders, List<PersistedRefund> Refunds, bool FinancialEventPosted);
public sealed record PersistedRefund(Guid Id, List<RefundLine> Lines, List<Tender> Tenders, string Reason, DateTimeOffset CreatedAtUtc);
public sealed record CreateStudioSaleRequest(IReadOnlyList<CreateStudioSaleLineRequest> Lines);
public sealed record CreateStudioSaleLineRequest(string? Code, string Description, decimal UnitPrice, int Quantity);
public sealed record CompleteStudioSaleRequest(IReadOnlyList<StudioTenderRequest> Tenders, string? TerminalId);
public sealed record StudioTenderRequest(TenderKind Kind, decimal Amount, string? Reference);
public sealed record VoidStudioSaleRequest(string Reason);
public sealed record RefundStudioSaleRequest(IReadOnlyList<RefundStudioSaleLineRequest> Lines, IReadOnlyList<StudioTenderRequest> Tenders, string Reason);
public sealed record RefundStudioSaleLineRequest(Guid SaleLineId, decimal UnitPrice, int Quantity);
