using System.Text;
using Aegis.Pos.Core;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Pos;

public sealed class PosReceiptStore
{
    private readonly StudioDatabase database;

    public PosReceiptStore(StudioDatabase database)
    {
        this.database = database;
    }

    public PosReceiptDocument GetOrCreate(Sale sale, StudioJob job, string cashierUserId)
    {
        PosReceiptDocument? existing = GetBySale(sale.Id);
        if (existing is not null) return existing;
        string receiptNumber = $"POS-{(sale.CompletedAtUtc ?? sale.CreatedAtUtc):yyyyMMdd}-{sale.Id.ToString("N")[..8].ToUpperInvariant()}";
        PosReceiptDocument receipt = new(Guid.NewGuid(),sale.Id,Guid.Parse(sale.ReferenceId),receiptNumber,job.ClientName,job.Title,cashierUserId,"JMD",sale.Lines.Select(x=>new PosReceiptLine(x.Description,x.Quantity,x.UnitPrice,x.Total)).ToList(),sale.Tenders.Select(x=>new PosReceiptTender(x.Kind.ToString(),x.Amount,x.Reference)).ToList(),sale.Total,sale.CompletedAtUtc??DateTimeOffset.UtcNow);
        using NpgsqlConnection connection=database.OpenConnection(); using NpgsqlTransaction transaction=connection.BeginTransaction();
        using(NpgsqlCommand command=connection.CreateCommand()){command.Transaction=transaction;command.CommandText="insert into studio_pos_receipts(id,sale_id,job_id,receipt_number,client_name,job_title,cashier_user_id,currency,total,created_at_utc) values ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)";object?[] values=[receipt.Id,receipt.SaleId,receipt.JobId,receipt.ReceiptNumber,receipt.ClientName,receipt.JobTitle,receipt.CashierUserId,receipt.Currency,receipt.Total,receipt.CreatedAtUtc];foreach(object? value in values)command.Parameters.AddWithValue(value??DBNull.Value);command.ExecuteNonQuery();}
        int index=0; foreach(PosReceiptLine line in receipt.Lines){using NpgsqlCommand command=connection.CreateCommand();command.Transaction=transaction;command.CommandText="insert into studio_pos_receipt_lines(id,receipt_id,description,quantity,unit_price,total,sort_order) values ($1,$2,$3,$4,$5,$6,$7)";command.Parameters.AddWithValue(Guid.NewGuid());command.Parameters.AddWithValue(receipt.Id);command.Parameters.AddWithValue(line.Description);command.Parameters.AddWithValue(line.Quantity);command.Parameters.AddWithValue(line.UnitPrice);command.Parameters.AddWithValue(line.Total);command.Parameters.AddWithValue(index++);command.ExecuteNonQuery();}
        index=0; foreach(PosReceiptTender tender in receipt.Tenders){using NpgsqlCommand command=connection.CreateCommand();command.Transaction=transaction;command.CommandText="insert into studio_pos_receipt_tenders(id,receipt_id,kind,amount,reference,sort_order) values ($1,$2,$3,$4,$5,$6)";command.Parameters.AddWithValue(Guid.NewGuid());command.Parameters.AddWithValue(receipt.Id);command.Parameters.AddWithValue(tender.Kind);command.Parameters.AddWithValue(tender.Amount);command.Parameters.AddWithValue((object?)tender.Reference??DBNull.Value);command.Parameters.AddWithValue(index++);command.ExecuteNonQuery();}
        transaction.Commit(); return receipt;
    }

    public PosReceiptDocument? GetBySale(Guid saleId)
    {
        using NpgsqlConnection connection=database.OpenConnection(); using NpgsqlCommand command=connection.CreateCommand(); command.CommandText="select id,sale_id,job_id,receipt_number,client_name,job_title,cashier_user_id,currency,total,created_at_utc from studio_pos_receipts where sale_id=$1";command.Parameters.AddWithValue(saleId);using NpgsqlDataReader reader=command.ExecuteReader();if(!reader.Read())return null;Guid receiptId=reader.GetGuid(0);PosReceiptDocument header=new(receiptId,reader.GetGuid(1),reader.GetGuid(2),reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetString(6),reader.GetString(7),[],[],reader.GetDecimal(8),reader.GetFieldValue<DateTimeOffset>(9));reader.Close();
        List<PosReceiptLine> lines=[];using(NpgsqlCommand q=connection.CreateCommand()){q.CommandText="select description,quantity,unit_price,total from studio_pos_receipt_lines where receipt_id=$1 order by sort_order";q.Parameters.AddWithValue(receiptId);using NpgsqlDataReader r=q.ExecuteReader();while(r.Read())lines.Add(new PosReceiptLine(r.GetString(0),r.GetInt32(1),r.GetDecimal(2),r.GetDecimal(3)));}
        List<PosReceiptTender> tenders=[];using(NpgsqlCommand q=connection.CreateCommand()){q.CommandText="select kind,amount,reference from studio_pos_receipt_tenders where receipt_id=$1 order by sort_order";q.Parameters.AddWithValue(receiptId);using NpgsqlDataReader r=q.ExecuteReader();while(r.Read())tenders.Add(new PosReceiptTender(r.GetString(0),r.GetDecimal(1),r.IsDBNull(2)?null:r.GetString(2)));}
        return header with { Lines=lines,Tenders=tenders };
    }

    public string RenderText(PosReceiptDocument receipt)
    {
        StringBuilder builder=new();builder.AppendLine("BRYAN'S STUDIO");builder.AppendLine(receipt.ReceiptNumber);builder.AppendLine(receipt.CreatedAtUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm"));builder.AppendLine($"Client: {receipt.ClientName}");builder.AppendLine($"Job: {receipt.JobTitle}");builder.AppendLine(new string('-',36));foreach(PosReceiptLine line in receipt.Lines){builder.AppendLine($"{line.Quantity} x {line.Description}");builder.AppendLine($"    {line.UnitPrice:N2}   {line.Total:N2}");}builder.AppendLine(new string('-',36));builder.AppendLine($"TOTAL {receipt.Total:N2} {receipt.Currency}");foreach(PosReceiptTender tender in receipt.Tenders)builder.AppendLine($"{tender.Kind}: {tender.Amount:N2}{(string.IsNullOrWhiteSpace(tender.Reference)?string.Empty:" / "+tender.Reference)}");builder.AppendLine($"Cashier: {receipt.CashierUserId}");builder.AppendLine("Thank you.");return builder.ToString();
    }
}

public sealed record PosReceiptDocument(Guid Id,Guid SaleId,Guid JobId,string ReceiptNumber,string ClientName,string JobTitle,string CashierUserId,string Currency,List<PosReceiptLine> Lines,List<PosReceiptTender> Tenders,decimal Total,DateTimeOffset CreatedAtUtc);
public sealed record PosReceiptLine(string Description,int Quantity,decimal UnitPrice,decimal Total);
public sealed record PosReceiptTender(string Kind,decimal Amount,string? Reference);
