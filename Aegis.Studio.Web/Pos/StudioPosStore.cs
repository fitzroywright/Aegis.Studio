using Aegis.Pos.Core;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Npgsql;
using Aegis.Studio.Web.Production;

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

    public Sale Create(Guid jobId, CreateStudioSaleRequest request, StudioStore studio, string userId, bool allowPriceOverride = false)
    {
        using var creationLock=request.ClientRequestId is Guid requestId?new PosOperationLock(database,"POSCreate:"+jobId+":"+requestId):null;
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

        if(request.ApplyToJobBalance && (request.Lines.Any(l=>l.Code!="BALANCE"||l.Quantity!=1)||request.Lines.Count!=1))throw new InvalidOperationException("Balance payments require a single balance payment line.");
        Sale sale = new(Guid.NewGuid(), "StudioJob", jobId.ToString("D"), DateTimeOffset.UtcNow);
        var snapshots=new List<(PosCatalogItem Item,decimal Net,CatalogProductionSnapshot[] Frozen)?>();
        var catalog=new PosCatalogStore(database);
        foreach (CreateStudioSaleLineRequest line in request.Lines)
        {
            PosCatalogItem? item=line.CatalogItemId is Guid catalogId?catalog.Get(catalogId):catalog.GetAll().FirstOrDefault(p=>p.Code.Equals(line.Code,StringComparison.OrdinalIgnoreCase));
            if(item is null)
            {
                if(!request.ApplyToJobBalance&&!allowPriceOverride)throw new UnauthorizedAccessException("Manual charges require price-override permission.");
                snapshots.Add(null);sale.AddLine(SaleLine.Create(line.Code??string.Empty,line.Description,line.UnitPrice,line.Quantity));continue;
            }
            if(!item.IsActive)throw new ArgumentException("Catalog item is inactive.");
            decimal gross=decimal.Round(item.UnitPrice*(1+item.TaxRate/100),2,MidpointRounding.AwayFromZero);
            using(var agreements=database.OpenConnection())using(var price=CommercialTemplateStore.Command(agreements,null,"select confirmed_unit_price from studio_job_catalog_agreements where job_id=$1 and catalog_item_id=$2 and confirmed_at is not null and sale_line_id is null order by id limit 1",jobId,item.Id))
                if(price.ExecuteScalar() is decimal agreed)gross=agreed;
            decimal charged=CommercialRules.Price(gross,line.UnitPrice,line.DiscountPercent,allowPriceOverride);
            decimal net=decimal.Round(charged/(1+item.TaxRate/100),2,MidpointRounding.AwayFromZero);
            snapshots.Add((item,net,CatalogWorkOrders.Freeze(item,database)));
            sale.AddLine(SaleLine.Create(item.Code,item.Name,charged,line.Quantity));
        }
        Save(sale, false, request.ClientRequestId,request.ApplyToJobBalance,request.Lines,snapshots);
        audit.Record("SaleCreated", sale.Id, jobId, userId, sale.Total, $"{sale.Lines.Count} line(s)");
        return sale;
    }

    public Sale Complete(Guid saleId, CompleteStudioSaleRequest request, StudioStore studio, string cashierUserId)
    {
        using var operationLock=new PosOperationLock(database,"POS:"+saleId.ToString("D"));
        PersistedSale snapshot = GetSnapshot(saleId) ?? throw new KeyNotFoundException("Sale was not found.");
        Sale sale = Rehydrate(snapshot);
        Guid jobId = Guid.Parse(sale.ReferenceId);
        using var jobLock=new PosOperationLock(database,"POSAgreement:"+jobId);
        using(var agreementConnection=database.OpenConnection())
        using(var agreementGuard=CommercialTemplateStore.Command(agreementConnection,null,"select exists(select 1 from studio_catalog_line_snapshots snap join studio_pos_sale_lines l on l.id=snap.sale_line_id join studio_job_catalog_agreements a on a.id=snap.agreement_id where l.sale_id=$1 and a.sale_line_id is not null and a.sale_line_id<>l.id)",sale.Id))
            if((bool)agreementGuard.ExecuteScalar()!)throw new InvalidOperationException("This confirmed agreement was already sold. Refresh the order before taking payment.");
        if (sale.Status == SaleStatus.Completed) { EnsureFinancialEventPosted(sale, snapshot, studio); ProductionOrderLinker.SyncCompletedSale(database,sale.Id,cashierUserId); return sale; }
        if (sale.Status == SaleStatus.Voided) throw new InvalidOperationException("A voided sale cannot be completed.");
        if (request.Tenders is null || request.Tenders.Count == 0) throw new InvalidOperationException("At least one tender is required.");
        Tender[] tenders = request.Tenders.Select(x => Tender.Create(x.Kind, x.Amount, x.Reference)).ToArray();
        if (sale.Status == SaleStatus.Draft) sale.Submit(DateTimeOffset.UtcNow);
        sale.Complete(tenders, DateTimeOffset.UtcNow, cashierUserId, request.TerminalId ?? "StudioWeb");
        snapshot = Save(sale, snapshot.FinancialEventPosted, snapshot.ClientRequestId);
        EnsureFinancialEventPosted(sale, snapshot, studio);
        ProductionOrderLinker.SyncCompletedSale(database,sale.Id,cashierUserId);
        audit.Record("SaleCompleted", sale.Id, jobId, cashierUserId, sale.Total, string.Join(", ", sale.Tenders.Select(t => $"{t.Kind}:{t.Amount:N2}")));
        return sale;
    }

    public Sale Void(Guid saleId, string userId, string reason)
    {
        using var operationLock=new PosOperationLock(database,"POS:"+saleId.ToString("D"));
        PersistedSale snapshot = GetSnapshot(saleId) ?? throw new KeyNotFoundException("Sale was not found.");
        Sale sale = Rehydrate(snapshot);
        sale.Void(userId, DateTimeOffset.UtcNow, reason);
        Save(sale, snapshot.FinancialEventPosted, snapshot.ClientRequestId);
        audit.Record("SaleVoided", sale.Id, Guid.Parse(sale.ReferenceId), userId, sale.Total, reason);
        return sale;
    }

    public Sale Refund(Guid saleId, RefundStudioSaleRequest request, StudioStore studio, string userId)
    {
        using var operationLock=new PosOperationLock(database,"POS:"+saleId.ToString("D"));
        PersistedSale snapshot = GetSnapshot(saleId) ?? throw new KeyNotFoundException("Sale was not found.");
        Sale sale = Rehydrate(snapshot);
        if(request.OperationId.HasValue&&sale.Refunds.Any(r=>r.Id==request.OperationId.Value)){EnsureRefundFinancialEvents(sale,snapshot,studio,userId);ProductionOrderLinker.SyncCompletedSale(database,saleId,userId);return sale;}
        if (request.Lines is null || request.Lines.Count == 0) throw new InvalidOperationException("At least one refund line is required.");
        if (request.Tenders is null || request.Tenders.Count == 0) throw new InvalidOperationException("At least one refund tender is required.");
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new InvalidOperationException("Refund reason is required.");
        RefundLine[] lines = request.Lines.Select(x => new RefundLine(x.SaleLineId, x.UnitPrice, x.Quantity)).ToArray();
        Tender[] tenders = request.Tenders.Select(x => Tender.Create(x.Kind, x.Amount, x.Reference)).ToArray();
        Refund refund = new(request.OperationId??Guid.NewGuid(), sale.Id, lines, tenders, request.Reason.Trim(), DateTimeOffset.UtcNow);
        sale.RecordRefund(refund);
        Save(sale, snapshot.FinancialEventPosted, snapshot.ClientRequestId);
        Guid jobId = Guid.Parse(sale.ReferenceId);
        studio.AddFinancialEvent(jobId, new AddFinancialEventRequest(snapshot.AppliesToBalance?"Refund":"POS Refund", -refund.Total, $"Refund {refund.Id}: {refund.Reason}",string.Join(" + ",refund.Tenders.Select(t=>t.Kind.ToString()).Distinct())),userId,refund.Id);
        ProductionOrderLinker.SyncCompletedSale(database,sale.Id,userId);
        audit.Record("RefundCompleted", sale.Id, jobId, userId, -refund.Total, refund.Reason);
        return sale;
    }

    public PosDiagnosticsSnapshot GetDiagnostics()
    {
        IReadOnlyList<Sale> sales = GetAll();
        return new PosDiagnosticsSnapshot(sales.Count, sales.Count(x => x.Status == SaleStatus.Draft), sales.Count(x => x.Status == SaleStatus.Completed), sales.Count(x => x.Status == SaleStatus.Voided), sales.Where(x => x.Status == SaleStatus.Completed).Sum(x => x.Total), sales.SelectMany(x => x.Refunds).Sum(x => x.Total), sales.OrderByDescending(x => x.CreatedAtUtc).FirstOrDefault()?.CreatedAtUtc);
    }

    private static void EnsureRefundFinancialEvents(Sale sale,PersistedSale snapshot,StudioStore studio,string actor)
    {
        foreach(var refund in sale.Refunds)studio.AddFinancialEvent(Guid.Parse(sale.ReferenceId),new AddFinancialEventRequest(snapshot.AppliesToBalance?"Refund":"POS Refund",-refund.Total,$"Refund {refund.Id}: {refund.Reason}",string.Join(" + ",refund.Tenders.Select(t=>t.Kind.ToString()).Distinct())),actor,refund.Id);
    }
    private void EnsureFinancialEventPosted(Sale sale, PersistedSale snapshot, StudioStore studio)
    {
        EnsureRefundFinancialEvents(sale,snapshot,studio,sale.CashierUserId??"Studio");
        if (snapshot.FinancialEventPosted) return;
        Guid jobId = Guid.Parse(sale.ReferenceId);
        string tenderSummary = string.Join(", ", sale.Tenders.Select(x => $"{x.Kind} {x.Amount:N2}"));
        studio.AddFinancialEvent(jobId, new AddFinancialEventRequest(snapshot.AppliesToBalance?"Payment":"POS Sale", sale.Total, $"Sale {sale.Id}: {tenderSummary}",string.Join(" + ",sale.Tenders.Select(t=>t.Kind.ToString()).Distinct())),sale.CashierUserId,sale.Id);
        Save(sale, true, snapshot.ClientRequestId);
    }

    private PersistedSale Save(Sale sale, bool financialEventPosted, Guid? clientRequestId,bool? applyToBalance=null,IReadOnlyList<CreateStudioSaleLineRequest>? sourceLines=null,IReadOnlyList<(PosCatalogItem Item,decimal Net,CatalogProductionSnapshot[] Frozen)?>? catalogSnapshots=null)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlTransaction transaction = connection.BeginTransaction();
        using (NpgsqlCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                insert into studio_pos_sales(id,reference_type,reference_id,created_at_utc,submitted_at_utc,completed_at_utc,voided_at_utc,voided_by,void_reason,cashier_user_id,terminal_id,status,financial_event_posted,client_request_id,applies_to_balance)
                values ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,coalesce($15,false))
                on conflict(id) do update set submitted_at_utc=excluded.submitted_at_utc,completed_at_utc=excluded.completed_at_utc,voided_at_utc=excluded.voided_at_utc,voided_by=excluded.voided_by,void_reason=excluded.void_reason,cashier_user_id=excluded.cashier_user_id,terminal_id=excluded.terminal_id,status=excluded.status,financial_event_posted=excluded.financial_event_posted,client_request_id=excluded.client_request_id,applies_to_balance=coalesce($15,studio_pos_sales.applies_to_balance)
                """;
            object?[] values = [sale.Id,sale.ReferenceType,sale.ReferenceId,sale.CreatedAtUtc,(object?)sale.SubmittedAtUtc??DBNull.Value,(object?)sale.CompletedAtUtc??DBNull.Value,(object?)sale.VoidedAtUtc??DBNull.Value,(object?)sale.VoidedBy??DBNull.Value,(object?)sale.VoidReason??DBNull.Value,(object?)sale.CashierUserId??DBNull.Value,(object?)sale.TerminalId??DBNull.Value,(int)sale.Status,financialEventPosted,(object?)clientRequestId??DBNull.Value,(object?)applyToBalance??DBNull.Value];
            foreach (object? value in values) command.Parameters.AddWithValue(value ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        DeleteChildren(connection, transaction, sale.Id);
        foreach (SaleLine line in sale.Lines)
        {
            using NpgsqlCommand command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "insert into studio_pos_sale_lines(id,sale_id,code,description,unit_price,quantity) values ($1,$2,$3,$4,$5,$6) on conflict(id) do update set code=excluded.code,description=excluded.description,unit_price=excluded.unit_price,quantity=excluded.quantity";
            command.Parameters.AddWithValue(line.Id); command.Parameters.AddWithValue(sale.Id); command.Parameters.AddWithValue(line.Code); command.Parameters.AddWithValue(line.Description); command.Parameters.AddWithValue(line.UnitPrice); command.Parameters.AddWithValue(line.Quantity); command.ExecuteNonQuery();
        }
        if(sourceLines is not null)
        {
            for(int i=0;i<sourceLines.Count;i++)
            {
                var request=sourceLines[i];var line=sale.Lines.ElementAt(i);
                if(catalogSnapshots?[i] is {} frozen)
                {
                    CatalogWorkOrders.SnapshotLine(connection,transaction,line.Id,frozen.Item,frozen.Net,request.Specifications,frozen.Frozen);
                    if(request.AgreementId is Guid agreement)
                    {
                        using var agreementCheck=CommercialTemplateStore.Command(connection,transaction,"select count(*)::int from studio_job_catalog_agreements where id=$1 and job_id=$2 and catalog_item_id=$3 and quantity=$4 and sale_line_id is null and specifications is not distinct from $5",agreement,Guid.Parse(sale.ReferenceId),frozen.Item.Id,line.Quantity,request.Specifications);
                        if((int)agreementCheck.ExecuteScalar()! !=1)throw new ArgumentException("The agreed product changed or has already been sold. Refresh the order.");
                        CommercialTemplateStore.Execute(connection,transaction,"update studio_catalog_line_snapshots set agreement_id=$2 where sale_line_id=$1",line.Id,agreement);
                    }
                }
                if(!ProductionOrderLinker.RequiresOutput(line.Code)&&request.SourceAssetId is null&&!(catalogSnapshots?[i]?.Frozen.Any(p=>p.Product.RequiresProduction)??false))continue;
                Guid? approved=null;
                if(request.SourceAssetId is Guid asset)
                {
                    using var source=new NpgsqlCommand("select approved_version_id from studio_media_assets where id=$1 and job_id=$2",connection,transaction);source.Parameters.AddWithValue(asset);source.Parameters.AddWithValue(Guid.Parse(sale.ReferenceId));
                    object? version=source.ExecuteScalar();if(version is null)throw new ArgumentException("Order image must belong to this job.");approved=version is Guid id?id:null;
                }
                using var binding=new NpgsqlCommand("insert into studio_pos_line_media(sale_line_id,asset_id,source_version_id) values($1,$2,$3) on conflict do nothing",connection,transaction);
                foreach(var v in new object?[]{line.Id,request.SourceAssetId,approved})binding.Parameters.AddWithValue(v??DBNull.Value);binding.ExecuteNonQuery();
            }
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
        string[] deletes=["delete from studio_pos_refund_tenders where refund_id in (select id from studio_pos_refunds where sale_id=$1)","delete from studio_pos_refund_lines where refund_id in (select id from studio_pos_refunds where sale_id=$1)","delete from studio_pos_refunds where sale_id=$1","delete from studio_pos_tenders where sale_id=$1"];
        foreach(string sql in deletes){using var command=new NpgsqlCommand(sql,connection,transaction);command.Parameters.AddWithValue(saleId);command.ExecuteNonQuery();}
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
        command.CommandText="select id,reference_type,reference_id,created_at_utc,submitted_at_utc,completed_at_utc,voided_at_utc,voided_by,void_reason,cashier_user_id,terminal_id,status,financial_event_posted,client_request_id,applies_to_balance from studio_pos_sales where id=$1"; command.Parameters.AddWithValue(saleId);
        using NpgsqlDataReader reader=command.ExecuteReader(); if(!reader.Read()) return null;
        PersistedSale header = new(reader.GetGuid(0),reader.GetString(1),reader.GetString(2),reader.GetFieldValue<DateTimeOffset>(3),reader.IsDBNull(4)?null:reader.GetFieldValue<DateTimeOffset>(4),reader.IsDBNull(5)?null:reader.GetFieldValue<DateTimeOffset>(5),reader.IsDBNull(6)?null:reader.GetFieldValue<DateTimeOffset>(6),reader.IsDBNull(7)?null:reader.GetString(7),reader.IsDBNull(8)?null:reader.GetString(8),reader.IsDBNull(9)?null:reader.GetString(9),reader.IsDBNull(10)?null:reader.GetString(10),(SaleStatus)reader.GetInt32(11),[],[],[],reader.GetBoolean(12),reader.IsDBNull(13)?null:reader.GetGuid(13)); header=header with{AppliesToBalance=reader.GetBoolean(14)};reader.Close();
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

public sealed record PersistedSale(Guid Id,string ReferenceType,string ReferenceId,DateTimeOffset CreatedAtUtc,DateTimeOffset? SubmittedAtUtc,DateTimeOffset? CompletedAtUtc,DateTimeOffset? VoidedAtUtc,string? VoidedBy,string? VoidReason,string? CashierUserId,string? TerminalId,SaleStatus Status,List<SaleLine> Lines,List<Tender> Tenders,List<PersistedRefund> Refunds,bool FinancialEventPosted,Guid? ClientRequestId=null,bool AppliesToBalance=false);
public sealed record PersistedRefund(Guid Id,List<RefundLine> Lines,List<Tender> Tenders,string Reason,DateTimeOffset CreatedAtUtc);
public sealed record CreateStudioSaleRequest(IReadOnlyList<CreateStudioSaleLineRequest> Lines,Guid? ClientRequestId=null,bool ApplyToJobBalance=false);
public sealed record CreateStudioSaleLineRequest(string? Code,string Description,decimal UnitPrice,int Quantity,Guid? SourceAssetId=null,Guid? CatalogItemId=null,string? Specifications=null,decimal DiscountPercent=0,Guid? AgreementId=null);
public sealed record CompleteStudioSaleRequest(IReadOnlyList<StudioTenderRequest> Tenders,string? TerminalId);
public sealed record StudioTenderRequest(TenderKind Kind,decimal Amount,string? Reference);
public sealed record VoidStudioSaleRequest(string Reason,Guid? OperationId=null);
public sealed record RefundStudioSaleRequest(IReadOnlyList<RefundStudioSaleLineRequest> Lines,IReadOnlyList<StudioTenderRequest> Tenders,string Reason,Guid? OperationId=null);
public sealed record RefundStudioSaleLineRequest(Guid SaleLineId,decimal UnitPrice,int Quantity);
public sealed record PosDiagnosticsSnapshot(int TotalSales,int DraftSales,int CompletedSales,int VoidedSales,decimal GrossCompleted,decimal Refunded,DateTimeOffset? LastSaleAtUtc);

