using System.Security.Claims;
using System.Text.Json;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Workflow;
using Npgsql;

namespace Aegis.Studio.Web.Finance;

public sealed record FinanceAuthorizationRequest(decimal Amount, string Kind, string Terms, DateOnly DueDate, string Reference, string Reason);
public sealed record FinanceReleaseDecision(decimal Balance, bool Authorized, Guid? AuthorizationId);

public static class FinanceReleaseRules
{
    public static bool Covers(decimal balance, decimal amount, bool revoked, bool sameCustomer)
        => balance > 0 && amount >= balance && !revoked && sameCustomer;

    public static void Validate(FinanceAuthorizationRequest request, DateOnly today)
    {
        if (request.Amount <= 0 || request.Amount > 999999999 || decimal.Round(request.Amount, 2) != request.Amount)
            throw new ArgumentException("Specify a positive JMD amount with at most two decimal places.");
        if (request.Kind is not ("CreditTerms" or "DeliveryAuthorization")) throw new ArgumentException("Choose credit terms or a specific delivery authorization.");
        if (request.DueDate < today) throw new ArgumentException("The payment due date cannot be in the past.");
        foreach (string value in new[] { request.Terms, request.Reference, request.Reason })
            if (string.IsNullOrWhiteSpace(value) || value.Length > 4000) throw new ArgumentException("Record terms, contractual reference and reason (at most 4000 characters each).");
    }
}

public static class FinanceReleaseStore
{
    // Receipts are Job-scoped, not invoice allocations. A receipt must be counted
    // once across the connected set of non-void invoices/Jobs. Until explicit
    // allocation exists, an unpaid connected invoice set requires Finance consent.
    public static decimal Balance(NpgsqlConnection c, NpgsqlTransaction? tx, Guid job)
    {
        using var q = WorkflowStore.Command(c, tx, """
            with recursive linked_jobs(id) as (
              select $1::uuid
              union
              select other.job_id from linked_jobs linked
                join studio_account_invoice_jobs current_link on current_link.job_id=linked.id
                join studio_account_invoices invoice on invoice.id=current_link.invoice_id and invoice.status<>'Void'
                join studio_account_invoice_jobs other on other.invoice_id=invoice.id
            ), invoices as (
              select i.* from studio_account_invoices i where i.status<>'Void'
                and exists(select 1 from studio_account_invoice_jobs ij join linked_jobs j on j.id=ij.job_id where ij.invoice_id=i.id)
            )
            select exists(select 1 from invoices where currency<>'JMD'),greatest(0,
              coalesce((select sum(case when lower(kind) in ('charge','adjustment','discount') then amount
                when lower(kind) in ('payment','deposit','refund','reversal') then -amount else 0 end)
                from studio_financial_events where job_id=$1),0),
              coalesce((select sum(total_amount) from invoices),0)-coalesce((select sum(e.amount)
                from studio_financial_events e join linked_jobs j on j.id=e.job_id
                where lower(e.kind) in ('payment','deposit','refund','reversal')),0))
            """, job);
        using var r=q.ExecuteReader();r.Read();
        if(r.GetBoolean(0))throw new ArgumentException("Resolve the non-JMD invoice before final release.");
        return r.GetDecimal(1);
    }

    public static FinanceReleaseDecision Decision(NpgsqlConnection c, NpgsqlTransaction? tx, Guid job)
    {
        decimal balance = Balance(c, tx, job);
        if (balance <= 0) return new(balance, true, null);
        using var q = WorkflowStore.Command(c, tx, """
            select a.id,a.authorized_amount,a.revoked_at_utc is not null,
              a.client_id is not distinct from j.client_id and a.client_name=j.client_name
            from studio_finance_authorizations a join studio_jobs j on j.id=a.job_id
            where a.job_id=$1 and a.scope='FinalDelivery' order by a.approved_at_utc desc
            """, job);
        using var r = q.ExecuteReader();
        while (r.Read())
            if (FinanceReleaseRules.Covers(balance, r.GetDecimal(1), r.GetBoolean(2), r.GetBoolean(3)))
                return new(balance, true, r.GetGuid(0));
        return new(balance, false, null);
    }

    public static void Require(NpgsqlConnection c, NpgsqlTransaction? tx, Guid job)
    {
        if (!Decision(c, tx, job).Authorized)
            throw new ArgumentException("A balance remains. Record payment or obtain Finance Manager credit/delivery authorization before final release.");
    }

    public static void Authorize(NpgsqlConnection c, NpgsqlTransaction tx, Guid job, Guid id, string actor, FinanceAuthorizationRequest request)
    {
        FinanceReleaseRules.Validate(request, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Jamaica")).DateTime));
        WorkflowStore.Sql(c, tx, """
            insert into studio_finance_authorizations(id,job_id,client_id,client_name,kind,authorized_amount,currency,scope,terms,payment_due_date,approved_by,reason,contract_reference)
            select $1,j.id,j.client_id,j.client_name,$3,$4,'JMD','FinalDelivery',$5,$6,$7,$8,$9 from studio_jobs j where j.id=$2
            """, id, job, request.Kind, request.Amount, request.Terms.Trim(), request.DueDate, actor, request.Reason.Trim(), request.Reference.Trim());
    }

    public static JsonElement History(NpgsqlConnection c, Guid job)
        => JsonSerializer.Deserialize<JsonElement>((string)WorkflowStore.Scalar(c, null,
            "select coalesce(jsonb_agg(to_jsonb(a) order by approved_at_utc desc),'[]'::jsonb)::text from studio_finance_authorizations a where job_id=$1", job)!);

    public static void MapStudioFinance(this WebApplication app)
    {
        app.MapGet("/api/jobs/{job:guid}/finance-release", (Guid job, ClaimsPrincipal user, WorkflowStore workflow, JobAccessService access, StudioDatabase db) => Run(() =>
        {
            if (!workflow.IsFinance(user) && !access.HasPermission(user, job, "ViewFinancials")) throw new UnauthorizedAccessException();
            using var c = db.OpenConnection();
            if (!WorkflowStore.Exists(c, null, "select exists(select 1 from studio_jobs where id=$1)", job)) return Results.NotFound();
            return Results.Ok(new { decision = Decision(c, null, job), history = History(c, job), canAuthorize = workflow.IsFinance(user) });
        })).RequireAuthorization();

        app.MapGet("/api/finance/receivables", (ClaimsPrincipal user, WorkflowStore workflow, StudioDatabase db) => Run(() =>
        {
            if (!workflow.IsFinance(user)) throw new UnauthorizedAccessException();
            using var c = db.OpenConnection();
            var jobs = new List<(Guid Id, string Title, string Customer, string Stage)>();
            using (var q = WorkflowStore.Command(c, null, "select id,title,client_name,status from studio_jobs order by created_at_utc desc"))
            using (var r = q.ExecuteReader()) while (r.Read()) jobs.Add((r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3)));
            return Results.Ok(jobs.Select(j => new { jobId = j.Id, j.Title, j.Customer, j.Stage, decision = Decision(c, null, j.Id), authorizations = History(c, j.Id) }).Where(j => j.decision.Balance > 0).ToArray());
        })).RequireAuthorization();
    }

    private static IResult Run(Func<IResult> action)
    {
        try { return action(); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
    }
}
