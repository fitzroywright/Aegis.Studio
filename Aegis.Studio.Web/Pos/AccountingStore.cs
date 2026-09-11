using Aegis.Pos.Core;
using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Pos;

public interface IAccountingGateway
{
    Task<AccountingExportResult> ExportAsync(AccountingBatch batch, CancellationToken cancellationToken);
}

public sealed class NullAccountingGateway : IAccountingGateway
{
    public Task<AccountingExportResult> ExportAsync(AccountingBatch batch, CancellationToken cancellationToken)
        => Task.FromResult(new AccountingExportResult(false, "NotConfigured", null, "No external accounting adapter is configured."));
}

public sealed class AccountingStore
{
    private readonly StudioDatabase database;
    private readonly IAccountingGateway gateway;

    public AccountingStore(StudioDatabase database, IAccountingGateway gateway)
    {
        this.database = database;
        this.gateway = gateway;
        EnsureSchema();
    }

    public RegisterSession OpenRegister(string terminalId, string cashierUserId, decimal openingFloat)
    {
        if (string.IsNullOrWhiteSpace(terminalId)) throw new InvalidOperationException("Terminal id is required.");
        if (string.IsNullOrWhiteSpace(cashierUserId)) throw new InvalidOperationException("Cashier user id is required.");
        if (openingFloat < 0) throw new InvalidOperationException("Opening float cannot be negative.");

        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlTransaction transaction = connection.BeginTransaction();
        using (NpgsqlCommand check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "select count(*) from studio_register_sessions where terminal_id=$1 and closed_at_utc is null";
            check.Parameters.AddWithValue(terminalId.Trim());
            if (Convert.ToInt32(check.ExecuteScalar()) > 0) throw new InvalidOperationException("This terminal already has an open register session.");
        }

        RegisterSession session = new(Guid.NewGuid(), terminalId.Trim(), cashierUserId.Trim(), openingFloat, DateTimeOffset.UtcNow, null, null, null, null, "Open");
        using (NpgsqlCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "insert into studio_register_sessions(id,terminal_id,cashier_user_id,opening_float,opened_at_utc,status) values($1,$2,$3,$4,$5,$6)";
            command.Parameters.AddWithValue(session.Id);
            command.Parameters.AddWithValue(session.TerminalId);
            command.Parameters.AddWithValue(session.CashierUserId);
            command.Parameters.AddWithValue(session.OpeningFloat);
            command.Parameters.AddWithValue(session.OpenedAtUtc);
            command.Parameters.AddWithValue(session.Status);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        return session;
    }

    public RegisterSession? GetOpenRegister(string terminalId)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id,terminal_id,cashier_user_id,opening_float,opened_at_utc,closed_at_utc,counted_cash,expected_cash,variance,status from studio_register_sessions where terminal_id=$1 and closed_at_utc is null order by opened_at_utc desc limit 1";
        command.Parameters.AddWithValue(terminalId);
        using NpgsqlDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadSession(reader) : null;
    }

    public RegisterCloseResult CloseRegister(Guid sessionId, decimal countedCash, string closedBy, string? note)
    {
        if (countedCash < 0) throw new InvalidOperationException("Counted cash cannot be negative.");
        RegisterSession session = GetSession(sessionId) ?? throw new KeyNotFoundException("Register session was not found.");
        if (session.ClosedAtUtc.HasValue) throw new InvalidOperationException("Register session is already closed.");

        DateTimeOffset closedAt = DateTimeOffset.UtcNow;
        ReconciliationSnapshot reconciliation = Reconcile(session, closedAt, countedCash);
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlTransaction transaction = connection.BeginTransaction();
        using (NpgsqlCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "update studio_register_sessions set closed_at_utc=$2,counted_cash=$3,expected_cash=$4,variance=$5,status='Closed',closed_by=$6,close_note=$7 where id=$1 and closed_at_utc is null";
            command.Parameters.AddWithValue(sessionId);
            command.Parameters.AddWithValue(closedAt);
            command.Parameters.AddWithValue(countedCash);
            command.Parameters.AddWithValue(reconciliation.ExpectedCash);
            command.Parameters.AddWithValue(reconciliation.CashVariance);
            command.Parameters.AddWithValue(string.IsNullOrWhiteSpace(closedBy) ? "Aegis.Studio" : closedBy.Trim());
            command.Parameters.AddWithValue((object?)note?.Trim() ?? DBNull.Value);
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Register session could not be closed.");
        }
        transaction.Commit();

        RegisterSession closed = GetSession(sessionId)!;
        return new RegisterCloseResult(closed, reconciliation);
    }

    public ReconciliationSnapshot Reconcile(Guid sessionId, decimal? countedCash = null)
    {
        RegisterSession session = GetSession(sessionId) ?? throw new KeyNotFoundException("Register session was not found.");
        DateTimeOffset end = session.ClosedAtUtc ?? DateTimeOffset.UtcNow;
        return Reconcile(session, end, countedCash ?? session.CountedCash);
    }

    public IReadOnlyList<AccountMapping> GetMappings()
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select mapping_key,account_code,description from studio_account_mappings order by mapping_key";
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<AccountMapping> values = [];
        while (reader.Read()) values.Add(new AccountMapping(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        return values;
    }

    public AccountMapping SaveMapping(AccountMapping mapping)
    {
        if (string.IsNullOrWhiteSpace(mapping.Key) || string.IsNullOrWhiteSpace(mapping.AccountCode)) throw new InvalidOperationException("Mapping key and account code are required.");
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "insert into studio_account_mappings(mapping_key,account_code,description) values($1,$2,$3) on conflict(mapping_key) do update set account_code=excluded.account_code,description=excluded.description";
        command.Parameters.AddWithValue(mapping.Key.Trim());
        command.Parameters.AddWithValue(mapping.AccountCode.Trim());
        command.Parameters.AddWithValue((object?)mapping.Description?.Trim() ?? DBNull.Value);
        command.ExecuteNonQuery();
        return new AccountMapping(mapping.Key.Trim(), mapping.AccountCode.Trim(), mapping.Description?.Trim());
    }

    public AccountingBatch GenerateBatch(Guid sessionId)
    {
        RegisterSession session = GetSession(sessionId) ?? throw new KeyNotFoundException("Register session was not found.");
        if (!session.ClosedAtUtc.HasValue) throw new InvalidOperationException("Register must be closed before generating an accounting batch.");

        AccountingBatch? existing = GetBatchForSession(sessionId);
        if (existing is not null) return existing;

        ReconciliationSnapshot reconciliation = Reconcile(sessionId);
        Dictionary<string, string> mappings = GetMappings().ToDictionary(x => x.Key, x => x.AccountCode, StringComparer.OrdinalIgnoreCase);
        if (!mappings.TryGetValue("Revenue:Sales", out string? salesAccount)) throw new InvalidOperationException("Account mapping Revenue:Sales is required.");

        List<AccountingBatchLine> lines = [];
        foreach (TenderReconciliation tender in reconciliation.Tenders.Where(x => x.NetAmount != 0))
        {
            string key = $"Tender:{tender.Kind}";
            if (!mappings.TryGetValue(key, out string? accountCode)) throw new InvalidOperationException($"Account mapping {key} is required.");
            AddSignedLine(lines, accountCode, key, tender.NetAmount, true);
        }

        decimal netRevenue = reconciliation.NetSales;
        AddSignedLine(lines, salesAccount, "Revenue:Sales", netRevenue, false);
        decimal debitTotal = lines.Sum(x => x.Debit);
        decimal creditTotal = lines.Sum(x => x.Credit);
        if (debitTotal != creditTotal) throw new InvalidOperationException($"Accounting batch is not balanced. Debits {debitTotal:N2}; credits {creditTotal:N2}.");

        AccountingBatch batch = new(Guid.NewGuid(), $"STUDIO-{session.ClosedAtUtc:yyyyMMdd}-{session.Id.ToString("N")[..8].ToUpperInvariant()}", session.Id, session.OpenedAtUtc, session.ClosedAtUtc.Value, DateTimeOffset.UtcNow, "Validated", debitTotal, creditTotal, lines, null, null);
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlTransaction transaction = connection.BeginTransaction();
        using (NpgsqlCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "insert into studio_accounting_batches(id,batch_reference,register_session_id,period_start_utc,period_end_utc,created_at_utc,status,debit_total,credit_total) values($1,$2,$3,$4,$5,$6,$7,$8,$9)";
            command.Parameters.AddWithValue(batch.Id); command.Parameters.AddWithValue(batch.Reference); command.Parameters.AddWithValue(batch.RegisterSessionId); command.Parameters.AddWithValue(batch.PeriodStartUtc); command.Parameters.AddWithValue(batch.PeriodEndUtc); command.Parameters.AddWithValue(batch.CreatedAtUtc); command.Parameters.AddWithValue(batch.Status); command.Parameters.AddWithValue(batch.DebitTotal); command.Parameters.AddWithValue(batch.CreditTotal); command.ExecuteNonQuery();
        }
        foreach (AccountingBatchLine line in lines)
        {
            using NpgsqlCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "insert into studio_accounting_batch_lines(id,batch_id,account_code,description,debit,credit) values($1,$2,$3,$4,$5,$6)";
            command.Parameters.AddWithValue(line.Id); command.Parameters.AddWithValue(batch.Id); command.Parameters.AddWithValue(line.AccountCode); command.Parameters.AddWithValue(line.Description); command.Parameters.AddWithValue(line.Debit); command.Parameters.AddWithValue(line.Credit); command.ExecuteNonQuery();
        }
        transaction.Commit();
        return batch;
    }

    public async Task<AccountingExportResult> ExportBatchAsync(Guid batchId, CancellationToken cancellationToken)
    {
        AccountingBatch batch = GetBatch(batchId) ?? throw new KeyNotFoundException("Accounting batch was not found.");
        if (batch.Status.Equals("Exported", StringComparison.OrdinalIgnoreCase)) return new AccountingExportResult(true, "AlreadyExported", batch.ExternalReference, "Batch was already exported; duplicate export prevented.");
        AccountingExportResult result = await gateway.ExportAsync(batch, cancellationToken);
        if (!result.Success) return result;

        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "update studio_accounting_batches set status='Exported',exported_at_utc=$2,external_reference=$3 where id=$1 and status<>'Exported'";
        command.Parameters.AddWithValue(batchId);
        command.Parameters.AddWithValue(DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue((object?)result.ExternalReference ?? DBNull.Value);
        command.ExecuteNonQuery();
        return result;
    }

    public AccountingBatch? GetBatch(Guid batchId)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id,batch_reference,register_session_id,period_start_utc,period_end_utc,created_at_utc,status,debit_total,credit_total,exported_at_utc,external_reference from studio_accounting_batches where id=$1";
        command.Parameters.AddWithValue(batchId);
        using NpgsqlDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        AccountingBatch header = new(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetFieldValue<DateTimeOffset>(3), reader.GetFieldValue<DateTimeOffset>(4), reader.GetFieldValue<DateTimeOffset>(5), reader.GetString(6), reader.GetDecimal(7), reader.GetDecimal(8), [], reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9), reader.IsDBNull(10) ? null : reader.GetString(10));
        reader.Close();
        using NpgsqlCommand linesCommand = connection.CreateCommand();
        linesCommand.CommandText = "select id,account_code,description,debit,credit from studio_accounting_batch_lines where batch_id=$1 order by account_code,id";
        linesCommand.Parameters.AddWithValue(batchId);
        using NpgsqlDataReader linesReader = linesCommand.ExecuteReader();
        List<AccountingBatchLine> lines = [];
        while (linesReader.Read()) lines.Add(new AccountingBatchLine(linesReader.GetGuid(0), linesReader.GetString(1), linesReader.GetString(2), linesReader.GetDecimal(3), linesReader.GetDecimal(4)));
        return header with { Lines = lines };
    }

    private AccountingBatch? GetBatchForSession(Guid sessionId)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id from studio_accounting_batches where register_session_id=$1 limit 1";
        command.Parameters.AddWithValue(sessionId);
        object? value = command.ExecuteScalar();
        return value is Guid id ? GetBatch(id) : null;
    }

    private RegisterSession? GetSession(Guid id)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id,terminal_id,cashier_user_id,opening_float,opened_at_utc,closed_at_utc,counted_cash,expected_cash,variance,status from studio_register_sessions where id=$1";
        command.Parameters.AddWithValue(id);
        using NpgsqlDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadSession(reader) : null;
    }

    private ReconciliationSnapshot Reconcile(RegisterSession session, DateTimeOffset end, decimal? countedCash)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select t.kind, coalesce(sum(t.amount),0)
            from studio_pos_tenders t
            join studio_pos_sales s on s.id=t.sale_id
            where s.status=$1 and s.terminal_id=$2 and s.completed_at_utc >= $3 and s.completed_at_utc <= $4
            group by t.kind
            order by t.kind;
            """;
        command.Parameters.AddWithValue((int)SaleStatus.Completed);
        command.Parameters.AddWithValue(session.TerminalId);
        command.Parameters.AddWithValue(session.OpenedAtUtc);
        command.Parameters.AddWithValue(end);
        Dictionary<TenderKind, decimal> sales = [];
        using (NpgsqlDataReader reader = command.ExecuteReader()) while (reader.Read()) sales[(TenderKind)reader.GetInt32(0)] = reader.GetDecimal(1);

        using NpgsqlCommand refundCommand = connection.CreateCommand();
        refundCommand.CommandText = """
            select rt.kind, coalesce(sum(rt.amount),0)
            from studio_pos_refund_tenders rt
            join studio_pos_refunds r on r.id=rt.refund_id
            join studio_pos_sales s on s.id=r.sale_id
            where s.terminal_id=$1 and r.created_at_utc >= $2 and r.created_at_utc <= $3
            group by rt.kind
            order by rt.kind;
            """;
        refundCommand.Parameters.AddWithValue(session.TerminalId);
        refundCommand.Parameters.AddWithValue(session.OpenedAtUtc);
        refundCommand.Parameters.AddWithValue(end);
        Dictionary<TenderKind, decimal> refunds = [];
        using (NpgsqlDataReader reader = refundCommand.ExecuteReader()) while (reader.Read()) refunds[(TenderKind)reader.GetInt32(0)] = reader.GetDecimal(1);

        TenderKind[] kinds = Enum.GetValues<TenderKind>();
        List<TenderReconciliation> tenders = kinds.Select(kind => new TenderReconciliation(kind, sales.GetValueOrDefault(kind), refunds.GetValueOrDefault(kind), sales.GetValueOrDefault(kind) - refunds.GetValueOrDefault(kind))).Where(x => x.SalesAmount != 0 || x.RefundAmount != 0).ToList();
        decimal grossSales = sales.Values.Sum();
        decimal refundTotal = refunds.Values.Sum();
        decimal netSales = grossSales - refundTotal;
        decimal cashNet = tenders.FirstOrDefault(x => x.Kind == TenderKind.Cash)?.NetAmount ?? 0;
        decimal expectedCash = session.OpeningFloat + cashNet;
        decimal? variance = countedCash.HasValue ? countedCash.Value - expectedCash : null;
        return new ReconciliationSnapshot(session.Id, session.OpenedAtUtc, end, session.OpeningFloat, grossSales, refundTotal, netSales, expectedCash, countedCash, variance, tenders);
    }

    private static RegisterSession ReadSession(NpgsqlDataReader reader)
        => new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetDecimal(3), reader.GetFieldValue<DateTimeOffset>(4), reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5), reader.IsDBNull(6) ? null : reader.GetDecimal(6), reader.IsDBNull(7) ? null : reader.GetDecimal(7), reader.IsDBNull(8) ? null : reader.GetDecimal(8), reader.GetString(9));

    private static void AddSignedLine(List<AccountingBatchLine> lines, string accountCode, string description, decimal amount, bool positiveIsDebit)
    {
        if (amount == 0) return;
        bool debit = amount > 0 ? positiveIsDebit : !positiveIsDebit;
        decimal absolute = Math.Abs(amount);
        lines.Add(new AccountingBatchLine(Guid.NewGuid(), accountCode, description, debit ? absolute : 0, debit ? 0 : absolute));
    }

    private void EnsureSchema()
    {
        const string sql = """
            create table if not exists studio_register_sessions
            (
                id uuid primary key,
                terminal_id text not null,
                cashier_user_id text not null,
                opening_float numeric(18,2) not null,
                opened_at_utc timestamptz not null,
                closed_at_utc timestamptz null,
                counted_cash numeric(18,2) null,
                expected_cash numeric(18,2) null,
                variance numeric(18,2) null,
                status text not null,
                closed_by text null,
                close_note text null
            );
            create unique index if not exists ux_studio_register_open_terminal on studio_register_sessions(terminal_id) where closed_at_utc is null;
            create index if not exists ix_studio_register_sessions_opened on studio_register_sessions(opened_at_utc desc);

            create table if not exists studio_account_mappings
            (
                mapping_key text primary key,
                account_code text not null,
                description text null
            );

            create table if not exists studio_accounting_batches
            (
                id uuid primary key,
                batch_reference text not null unique,
                register_session_id uuid not null unique references studio_register_sessions(id),
                period_start_utc timestamptz not null,
                period_end_utc timestamptz not null,
                created_at_utc timestamptz not null,
                status text not null,
                debit_total numeric(18,2) not null,
                credit_total numeric(18,2) not null,
                exported_at_utc timestamptz null,
                external_reference text null
            );
            create table if not exists studio_accounting_batch_lines
            (
                id uuid primary key,
                batch_id uuid not null references studio_accounting_batches(id) on delete cascade,
                account_code text not null,
                description text not null,
                debit numeric(18,2) not null,
                credit numeric(18,2) not null
            );
            create index if not exists ix_studio_accounting_batch_lines_batch on studio_accounting_batch_lines(batch_id);
            """;
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

public sealed record RegisterSession(Guid Id, string TerminalId, string CashierUserId, decimal OpeningFloat, DateTimeOffset OpenedAtUtc, DateTimeOffset? ClosedAtUtc, decimal? CountedCash, decimal? ExpectedCash, decimal? Variance, string Status);
public sealed record OpenRegisterRequest(string TerminalId, decimal OpeningFloat);
public sealed record CloseRegisterRequest(decimal CountedCash, string? Note);
public sealed record RegisterCloseResult(RegisterSession Session, ReconciliationSnapshot Reconciliation);
public sealed record TenderReconciliation(TenderKind Kind, decimal SalesAmount, decimal RefundAmount, decimal NetAmount);
public sealed record ReconciliationSnapshot(Guid RegisterSessionId, DateTimeOffset PeriodStartUtc, DateTimeOffset PeriodEndUtc, decimal OpeningFloat, decimal GrossSales, decimal RefundTotal, decimal NetSales, decimal ExpectedCash, decimal? CountedCash, decimal? CashVariance, IReadOnlyList<TenderReconciliation> Tenders);
public sealed record AccountMapping(string Key, string AccountCode, string? Description = null);
public sealed record AccountingBatchLine(Guid Id, string AccountCode, string Description, decimal Debit, decimal Credit);
public sealed record AccountingBatch(Guid Id, string Reference, Guid RegisterSessionId, DateTimeOffset PeriodStartUtc, DateTimeOffset PeriodEndUtc, DateTimeOffset CreatedAtUtc, string Status, decimal DebitTotal, decimal CreditTotal, IReadOnlyList<AccountingBatchLine> Lines, DateTimeOffset? ExportedAtUtc, string? ExternalReference);
public sealed record AccountingExportResult(bool Success, string Status, string? ExternalReference, string? Message);
