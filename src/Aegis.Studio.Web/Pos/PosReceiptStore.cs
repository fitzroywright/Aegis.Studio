using System.Text;
using Aegis.Pos.Core;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;

namespace Aegis.Studio.Web.Pos;

public sealed class PosReceiptStore
{
    private const string Collection = "pos-receipts";
    private readonly PostgresDocumentStore persistence;

    public PosReceiptStore(PostgresDocumentStore persistence)
    {
        this.persistence = persistence;
    }

    public PosReceiptDocument GetOrCreate(Sale sale, StudioJob job, string cashierUserId)
    {
        PosReceiptDocument? existing = persistence.GetAll<PosReceiptDocument>(Collection)
            .FirstOrDefault(receipt => receipt.SaleId == sale.Id);
        if (existing is not null)
        {
            return existing;
        }

        string receiptNumber = $"POS-{(sale.CompletedAtUtc ?? sale.CreatedAtUtc):yyyyMMdd}-{sale.Id.ToString("N")[..8].ToUpperInvariant()}";
        PosReceiptDocument receipt = new
        (
            Guid.NewGuid(),
            sale.Id,
            Guid.Parse(sale.ReferenceId),
            receiptNumber,
            job.ClientName,
            job.Title,
            cashierUserId,
            "JMD",
            sale.Lines.Select(line => new PosReceiptLine(line.Description, line.Quantity, line.UnitPrice, line.Total)).ToList(),
            sale.Tenders.Select(tender => new PosReceiptTender(tender.Kind.ToString(), tender.Amount, tender.Reference)).ToList(),
            sale.Total,
            sale.CompletedAtUtc ?? DateTimeOffset.UtcNow
        );
        persistence.Upsert(Collection, receipt.Id, receipt);
        return receipt;
    }

    public PosReceiptDocument? GetBySale(Guid saleId)
        => persistence.GetAll<PosReceiptDocument>(Collection).FirstOrDefault(receipt => receipt.SaleId == saleId);

    public string RenderText(PosReceiptDocument receipt)
    {
        StringBuilder builder = new();
        builder.AppendLine("BRYAN'S STUDIO");
        builder.AppendLine(receipt.ReceiptNumber);
        builder.AppendLine(receipt.CreatedAtUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm"));
        builder.AppendLine($"Client: {receipt.ClientName}");
        builder.AppendLine($"Job: {receipt.JobTitle}");
        builder.AppendLine(new string('-', 36));
        foreach (PosReceiptLine line in receipt.Lines)
        {
            builder.AppendLine($"{line.Quantity} x {line.Description}");
            builder.AppendLine($"    {line.UnitPrice:N2}   {line.Total:N2}");
        }
        builder.AppendLine(new string('-', 36));
        builder.AppendLine($"TOTAL {receipt.Total:N2} {receipt.Currency}");
        foreach (PosReceiptTender tender in receipt.Tenders)
        {
            builder.AppendLine($"{tender.Kind}: {tender.Amount:N2}{(string.IsNullOrWhiteSpace(tender.Reference) ? string.Empty : " / " + tender.Reference)}");
        }
        builder.AppendLine($"Cashier: {receipt.CashierUserId}");
        builder.AppendLine("Thank you.");
        return builder.ToString();
    }
}

public sealed record PosReceiptDocument
(
    Guid Id,
    Guid SaleId,
    Guid JobId,
    string ReceiptNumber,
    string ClientName,
    string JobTitle,
    string CashierUserId,
    string Currency,
    List<PosReceiptLine> Lines,
    List<PosReceiptTender> Tenders,
    decimal Total,
    DateTimeOffset CreatedAtUtc
);

public sealed record PosReceiptLine(string Description, int Quantity, decimal UnitPrice, decimal Total);
public sealed record PosReceiptTender(string Kind, decimal Amount, string? Reference);
