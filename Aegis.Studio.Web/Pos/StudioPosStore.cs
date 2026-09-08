using Aegis.Pos.Core;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Pos;

public sealed class StudioPosStore
{
    private readonly StudioDatabase database;
    private readonly PosAuditStore audit;

    public StudioPosStore(StudioDatabase database, PosAuditStore audit)
    {
        this.database = database;
        this.audit = audit;
    }

    public IReadOnlyList<Sale> GetForJob(Guid jobId)
        => GetSnapshots("where reference_type='StudioJob' and reference_id=$1", jobId.ToString("D")).Select(Rehydrate).ToList();

    public IReadOnlyList<Sale> GetAll() => GetSnapshots(string.Empty, null).Select(Rehydrate).ToList();

    public Sale? Get(Guid saleId)
    {
        PersistedSale? snapshot = GetSnapshot(saleId);
        return snapshot is null ? null : Rehydrate(snapshot);
    }

    public Sale Create(Guid jobId, CreateStudioSaleRequest request, StudioStore studio, string userId)
    {
        if (studio.GetJob(jobId) is null) throw new KeyNotFoundException("Job was not found.");
        if (request.Lines is null || request.Lines.Count == 0) throw new InvalidOperationException("At least one sale line is required.");

        if (request.ClientRequestId.HasValue)
        {
            using NpgsqlConnection connection = database.OpenConnection();
            using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = "select id from studio_pos_sales where reference_type='StudioJob' and reference_id=$1 and client_request_id=$2 limit 1";
            command.Parameters.AddWithValue(jobId.ToString("D"));
            command.Parameters.AddWithValue(request.ClientRequestId.Value);
            object? existingId = command.ExecuteScalar();
            if (existingId is Guid id) return Get(id)!;
        }

        Sale sale = new(Guid.NewGuid(), "StudioJob", jobId.ToString("D"), DateTimeOffset.UtcNow);
        foreach (CreateStudioSaleLineRequest line in request.Lines) sale.AddLine(SaleLine.Create(line.Code ?? string.Empty, line.Description, line.UnitPrice, line.Quantity));
        Save(sale, false, request.ClientRequestId);
        audit.Record("SaleCreated", sale.Id, jobId, userId, sale.Total, $"{sale.Lines.Count} line(s)");
        return sale;
    }

    public Sale Complete(Guid saleId, CompleteStudioSaleRequest request, StudioStore studio, string cashierUserId)
    {
        PersistedSale snapshot = GetSnapshot(saleId) ?? throw new KeyNotFoundException("Sale was not found.");
        Sale sale = Rehydrate(snapshot);
        Guid jobId = Guid.Parse(sale.ReferenceId);
        if (sale.Status == SaleStatus.Completed) { EnsureFinancialEventPosted(sale, snapshot, studio); return sale; }
        if (sale.Status == SaleStatus.Voided) throw new InvalidOperationException("A voided sale cannot be completed.");
        if (request.Tenders is null || request.Tenders.Count == 0) throw new InvalidOperationException("At least one tender is required.");
        Tender[] tenders = request.Tenders.Select(x => Tender.Create(x.Kind, x.Amount, x.Reference)).ToArray();
        if (sale.Status == SaleStatus.Draft) sale.Submit(DateTimeOffset.UtcNow);
        sale.Complete(tenders, DateTimeOffset.UtcNow, cashierUserId, request.TerminalId ?? "StudioWeb");
        snapshot = Save(sale, snapshot.FinancialEventPosted, snapshot.ClientRequestId);
        EnsureFinancialEventPosted(sale, snapshot, studio);
        audit.Record("SaleCompleted", sale.Id, jobId, cashierUserId, sale.Total, string.Join(", ", sale.Tenders.Select(t => $"{t.Kind}:{t.Amount:N2}")));
        return sale;
    }

    public Sale Void(Guid saleId, string userId, string reason)
    {
        PersistedSale snapshot = GetSnapshot(saleId) ?? throw new KeyNotFoundException("Sale was not found.");
        Sale sale = Rehydrate(snapshot);
        sale.Void(userId, DateTimeOffset.UtcNow, reason);
        Save(sale, snapshot.FinancialEventPosted, snapshot.ClientRequestId);
        audit.Record("SaleVoided", sale.Id, Guid.Parse(sale.ReferenceId), userId, sale.Total, reason);
        return sale;
    }

    public Sale Refund(Guid saleId, RefundStudioSaleRequest request, StudioStore studio, string userId)
    {
        PersistedSale snapshot = GetSnapshot(saleId) ?? throw new KeyNotFoundException("Sale was not found.");
        Sale sale = Rehydrate(snapshot);
        if (request.Lines is null || request.Lines.Count == 0) throw new InvalidOperationException("At least one refund line is required.");
        if (request.Tenders is null || request.Tenders.Count == 0) throw new InvalidOperationException("At least one refund tender is required.");
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new InvalidOperationException("Refund reason is required.");
        RefundLine[] lines = request.Lines.Select(x => new RefundLine(x.SaleLineId, x.UnitPrice, x.Quantity)).ToArray();
        Tender[] tenders = request.Tenders.Select(x => Tender.Create(x.Kind, x.Amount, x.Reference)).ToArray();
        Refund refund = new(Guid.NewGuid(), sale.Id, lines, tenders, request.Reason.Trim(), DateTimeOffset.UtcNow);
        sale.RecordRefund(refund);
        Save(sale, snapshot.FinancialEventPosted, snapshot.ClientRequestId);
        Guid jobId = Guid.Parse(sale.ReferenceId);
        studio.AddFinancialEvent(jobId, new AddFinancialEventRequest("POS Refund", -refund.Total, $"Refund {refund.Id}: {refund.Reason}"));
        audit.Record("RefundCompleted", sale.Id, jobId, userId, -refund.Total, refund.Reason);
        return sale;
    }

    public PosDiagnosticsSnapshot GetDiagnostics()
    {
        IReadOnlyList<Sale> sales = GetAll();
        return new PosDiagnosticsSnapshot(sales.Count, sales.Count(x => x.Status == SaleStatus.Draft), sales.Count(x => x.Status == SaleStatus.Completed), sales.Count(x => x.Status == SaleStatus.Voided), sales.Where(x => x.Status == SaleStatus.Completed).Sum(x => x.Total), sales.SelectMany(x => x.Refunds).Sum(x => x.Total), sales.OrderByDescending(x => x.CreatedAtUtc).FirstOrDefault()?.CreatedAtUtc);
    }

    private void EnsureFinancialEventPosted(Sale sale, PersistedSale snapshot, StudioStore studio)
    {
        if (snapshot.FinancialEventPosted) return;
        Guid jobId = Guid.Parse(sale.ReferenceId);
        string tenderSummary = string.Join(", ", sale.Tenders.Select(x => $"{x.Kind} {x.Amount:N2}"));
        studio.AddFinancialEvent(jobId, new AddFinancialEventRequest("POS Sale", sale.Total, $"Sale {sale.Id}: {tenderSummary}"));
        Save(sale, true, snapshot.ClientRequestId);
    }

    private PersistedSale Save(Sale sale, bool financialEventPosted, Guid? clientRequestId)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlTransaction transaction = connection.BeginTransaction();
        using (NpgsqlCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                insert into studio_pos_sales(id,reference_type,reference_id,created_at_utc,submitted_at_utc,completed_at_utc,voided_at_utc,voided_by,void_reason,cashier_user_id,terminal_id,status,financial_event_posted,client_request_id)
                values ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14)
                on conflict(id) do update set submitted_at_utc=excluded.submitted_at_utc,completed_at_utc=excluded.completed_at_utc,voided_at_utc=excluded.voided_at_utc,voided_by=excluded.voided_by,void_reason=excluded.void_reason,cashier_user_id=excluded.cashier_user_id,terminal_id=excluded.terminal_id,status=excluded.status,financial_event_posted=excluded.financial_event_posted,client_request_id=excluded.client_request_id
                """;
            object?[] values = [sale.Id,sale.ReferenceType,sale.ReferenceId,sale.CreatedAtUtc,(object?)sale.SubmittedAtUtc??DBNull.Value,(object?)sale.CompletedAtUtc??DBNull.Value,(object?)sale.VoidedAtUtc??DBNull.Value,(object?)sale.VoidedBy??DBNull.Value,(object?)sale.VoidReason??DBNull.Value,(object?)sale.CashierUserId??DBNull.Value,(object?)sale.TerminalId??DBNull.Value,(int)sale.Status,financialEventPosted,(object?)clientRequestId??DBNull.Value];
            foreach (object? value in values) command.Parameters.AddWithValue(value ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        DeleteChildren(connection, transaction, sale.Id);
        foreach (SaleLine line in sale.Lines)
        {
            using NpgsqlCommand command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "insert into studio_pos_sale_lines(id,sale_id,code,description,unit_price,quantity) values ($1,$2,$3,$4,$5,$6)";
            command.Parameters.AddWithValue(line.Id); command.Parameters.AddWithValue(sale.Id); command.Parameters.AddWithValue(line.Code); command.Parameters.AddWithValue(line.Description); command.Parameters.AddWithValue(line.UnitPrice); command.Parameters.AddWithValue(line.Quantity); command.ExecuteNonQuery();
        }
        foreach (Tender tender in sale.Tenders)
        {
            using NpgsqlCommand command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "insert into studio_pos_tenders(id,sale_id,kind,amount,reference) values ($1,$2,$3,$4,$5)";
            command.Parameters.AddWithValue(tender.Id); command.Parameters.AddWithValue(sale.Id); command.Parameters.AddWithValue((int)tender.Kind); command.Parameters.AddWithValue(tender.Amount); command.Parameters.AddWithValue((object?)tender.Reference??DBNull.Value); command.ExecuteNonQuery();
        }
        foreach (Refund refund in sale.Refunds)
        {
            using (NpgsqlCommand command = connection.CreateCommand()) { command.Transaction=transaction; command.CommandText="insert into studio_pos_refunds(id,sale_id,reason,created_at_utc) values ($1,$2,$3,$4)"; command.Parameters.AddWithValue(refund.Id); command.Parameters.AddWithValue(sale.Id); command.Parameters.AddWithValue(refund.Reason); command.Parameters.AddWithValue(refund.CreatedAtUtc); command.ExecuteNonQuery(); }
            foreach (RefundLine line in refund.Lines) { using NpgsqlCommand command=connection.CreateCommand(); command.Transaction=transaction; command.CommandText="insert into studio_pos_refund_lines(id,refund_id,sale_line_id,unit_price,quantity) values ($1,$2,$3,$4,$5)"; command.Parameters.AddWithValue(Guid.NewGuid()); command.Parameters.AddWithValue(refund.Id); command.Parameters.AddWithValue(line.SaleLineId); command.Parameters.AddWithValue(line.UnitPrice); command.Parameters.AddWithValue(line.Quantity); command.ExecuteNonQuery(); }
            foreach (Tender tender in refund.Tenders) { using NpgsqlCommand command=connection.CreateCommand(); command.Transaction=transaction; command.CommandText="insert into studio_pos_refund_tenders(id,refund_id,kind,amount,reference) values ($1,$2,$3,$4,$5)"; command.Parameters.AddWithValue(tender.Id); command.Parameters.AddWithValue(refund.Id); command.Parameters.AddWithValue((int)tender.Kind); command.Parameters.AddWithValue(tender.Amount); command.Parameters.AddWithValue((object?)tender.Reference??DBNull.Value); command.ExecuteNonQuery(); }
        }
        transaction.Commit();
        return GetSnapshot(sale.Id)!;
    }

    private static void DeleteChildren(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid saleId)
    {
        using NpgsqlCommand command = connection.CreateCommand(); command.Transaction=transaction;
        command.CommandText = "delete from studio_pos_refund_tenders where refund_id in (select id from studio_pos_refunds where sale_id=$1); delete from studio_pos_refund_lines where refund_id in (select id from studio_pos_refunds where sale_id=$1); delete from studio_pos_refunds where sale_id=$1; delete from studio_pos_tenders where sale_id=$1; delete from studio_pos_sale_lines where sale_id=$1;";
        command.Parameters.AddWithValue(saleId); command.ExecuteNonQuery();
    }

    private IReadOnlyList<PersistedSale> GetSnapshots(string whereClause, string? referenceId)
    {
        using NpgsqlConnection connection = database.OpenConnection(); using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = $"select id from studio_pos_sales {whereClause} order by created_at_utc desc";
        if (referenceId is not null) command.Parameters.AddWithValue(referenceId);
        using NpgsqlDataReader reader = command.ExecuteReader(); List<Guid> ids=[]; while(reader.Read()) ids.Add(reader.GetGuid(0)); reader.Close();
        return ids.Select(id => GetSnapshot(id)!).Where(x=>x is not null).ToList();
    }

    private PersistedSale? GetSnapshot(Guid saleId)
    {
        using NpgsqlConnection connection = database.OpenConnection(); using NpgsqlCommand command=connection.CreateCommand();
        command.CommandText="select id,reference_type,reference_id,created_at_utc,submitted_at_utc,completed_at_utc,voided_at_utc,voided_by,void_reason,cashier_user_id,terminal_id,status,financial_event_posted,client_request_id from studio_pos_sales where id=$1"; command.Parameters.AddWithValue(saleId);
        using NpgsqlDataReader reader=command.ExecuteReader(); if(!reader.Read()) return null;
        PersistedSale header = new(reader.GetGuid(0),reader.GetString(1),reader.GetString(2),reader.GetFieldValue<DateTimeOffset>(3),reader.IsDBNull(4)?null:reader.GetFieldValue<DateTimeOffset>(4),reader.IsDBNull(5)?null:reader.GetFieldValue<DateTimeOffset>(5),reader.IsDBNull(6)?null:reader.GetFieldValue<DateTimeOffset>(6),reader.IsDBNull(7)?null:reader.GetString(7),reader.IsDBNull(8)?null:reader.GetString(8),reader.IsDBNull(9)?null:reader.GetString(9),reader.IsDBNull(10)?null:reader.GetString(10),(SaleStatus)reader.GetInt32(11),[],[],[],reader.GetBoolean(12),reader.IsDBNull(13)?null:reader.GetGuid(13)); reader.Close();
        List<SaleLine> lines=[]; using(NpgsqlCommand q=connection.CreateCommand()){q.CommandText="select id,code,description,unit_price,quantity from studio_pos_sale_lines where sale_id=$1";q.Parameters.AddWithValue(saleId);using NpgsqlDataReader r=q.ExecuteReader();while(r.Read())lines.Add(new SaleLine(r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetDecimal(3),r.GetInt32(4)));}
        List<Tender> tenders=[]; using(NpgsqlCommand q=connection.CreateCommand()){q.CommandText="select id,kind,amount,reference from studio_pos_tenders where sale_id=$1";q.Parameters.AddWithValue(saleId);using NpgsqlDataReader r=q.ExecuteReader();while(r.Read())tenders.Add(new Tender(r.GetGuid(0),(TenderKind)r.GetInt32(1),r.GetDecimal(2),r.IsDBNull(3)?null:r.GetString(3)));}
        List<PersistedRefund> refunds=[]; using(NpgsqlCommand q=connection.CreateCommand()){q.CommandText="select id,reason,created_at_utc from studio_pos_refunds where sale_id=$1 order by created_at_utc";q.Parameters.AddWithValue(saleId);using NpgsqlDataReader r=q.ExecuteReader();List<(Guid,string,DateTimeOffset)> rs=[];while(r.Read())rs.Add((r.GetGuid(0),r.GetString(1),r.GetFieldValue<DateTimeOffset>(2)));r.Close();foreach(var item in rs){List<RefundLine> refundLines=[];using(NpgsqlCommand l=connection.CreateCommand()){l.CommandText="select sale_line_id,unit_price,quantity from studio_pos_refund_lines where refund_id=$1";l.Parameters.AddWithValue(item.Item1);using NpgsqlDataReader lr=l.ExecuteReader();while(lr.Read())refundLines.Add(new RefundLine(lr.GetGuid(0),lr.GetDecimal(1),lr.GetInt32(2)));}List<Tender> refundTenders=[];using(NpgsqlCommand t=connection.CreateCommand()){t.CommandText="select id,kind,amount,reference from studio_pos_refund_tenders where refund_id=$1";t.Parameters.AddWithValue(item.Item1);using NpgsqlDataReader tr=t.ExecuteReader();while(tr.Read())refundTenders.Add(new Tender(tr.GetGuid(0),(TenderKind)tr.GetInt32(1),tr.GetDecimal(2),tr.IsDBNull(3)?null:tr.GetString(3)));}refunds.Add(new PersistedRefund(item.Item1,refundLines,refundTenders,item.Item2,item.Item3));}};
        return header with { Lines=lines, Tenders=tenders, Refunds=refunds };
    }

    private static Sale Rehydrate(PersistedSale snapshot)
    {
        Sale sale=new(snapshot.Id,snapshot.ReferenceType,snapshot.ReferenceId,snapshot.CreatedAtUtc); foreach(SaleLine line in snapshot.Lines)sale.AddLine(line); if(snapshot.Status!=SaleStatus.Draft)sale.Submit(snapshot.SubmittedAtUtc??snapshot.CreatedAtUtc); if(snapshot.Status==SaleStatus.Completed){sale.Complete(snapshot.Tenders,snapshot.CompletedAtUtc??snapshot.CreatedAtUtc,snapshot.CashierUserId??"Aegis.Studio",snapshot.TerminalId??"StudioWeb");foreach(PersistedRefund refund in snapshot.Refunds)sale.RecordRefund(new Refund(refund.Id,sale.Id,refund.Lines,refund.Tenders,refund.Reason,refund.CreatedAtUtc));}else if(snapshot.Status==SaleStatus.Voided)sale.Void(snapshot.VoidedBy??"Aegis.Studio",snapshot.VoidedAtUtc??snapshot.CreatedAtUtc,snapshot.VoidReason??"Voided"); return sale;
    }
}

public sealed record PersistedSale(Guid Id,string ReferenceType,string ReferenceId,DateTimeOffset CreatedAtUtc,DateTimeOffset? SubmittedAtUtc,DateTimeOffset? CompletedAtUtc,DateTimeOffset? VoidedAtUtc,string? VoidedBy,string? VoidReason,string? CashierUserId,string? TerminalId,SaleStatus Status,List<SaleLine> Lines,List<Tender> Tenders,List<PersistedRefund> Refunds,bool FinancialEventPosted,Guid? ClientRequestId=null);
public sealed record PersistedRefund(Guid Id,List<RefundLine> Lines,List<Tender> Tenders,string Reason,DateTimeOffset CreatedAtUtc);
public sealed record CreateStudioSaleRequest(IReadOnlyList<CreateStudioSaleLineRequest> Lines,Guid? ClientRequestId=null);
public sealed record CreateStudioSaleLineRequest(string? Code,string Description,decimal UnitPrice,int Quantity);
public sealed record CompleteStudioSaleRequest(IReadOnlyList<StudioTenderRequest> Tenders,string? TerminalId);
public sealed record StudioTenderRequest(TenderKind Kind,decimal Amount,string? Reference);
public sealed record VoidStudioSaleRequest(string Reason);
public sealed record RefundStudioSaleRequest(IReadOnlyList<RefundStudioSaleLineRequest> Lines,IReadOnlyList<StudioTenderRequest> Tenders,string Reason);
public sealed record RefundStudioSaleLineRequest(Guid SaleLineId,decimal UnitPrice,int Quantity);
public sealed record PosDiagnosticsSnapshot(int TotalSales,int DraftSales,int CompletedSales,int VoidedSales,decimal GrossCompleted,decimal Refunded,DateTimeOffset? LastSaleAtUtc);
