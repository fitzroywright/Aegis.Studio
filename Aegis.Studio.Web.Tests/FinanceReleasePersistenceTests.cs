using Aegis.Studio.Web.Finance;
using Npgsql;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class FinanceReleasePersistenceTests
{
    [StudioPostgresFact]
    public void ReleaseTracksRefundsClientChangesAndRevocationWithoutLosingReceivable()
    {
        // Connection-local temporary tables cannot touch installation data.
        using var c = new NpgsqlConnection(Environment.GetEnvironmentVariable("STUDIO_TEST_POSTGRES"));
        c.Open();
        using var schema = new NpgsqlCommand("""
            create temp table studio_jobs(id uuid primary key,client_id uuid,client_name text);
            create temp table studio_financial_events(job_id uuid,kind text,amount numeric);
            create temp table studio_account_invoices(id uuid,status text,currency text,total_amount numeric);
            create temp table studio_account_invoice_jobs(invoice_id uuid,job_id uuid);
            create temp table studio_finance_authorizations(id uuid,job_id uuid,client_id uuid,client_name text,scope text,authorized_amount numeric,revoked_at_utc timestamptz,approved_at_utc timestamptz);
            insert into studio_jobs values('11111111-1111-1111-1111-111111111111',null,'Client');
            insert into studio_financial_events values('11111111-1111-1111-1111-111111111111','Charge',1000),('11111111-1111-1111-1111-111111111111','Payment',250);
            """, c);
        schema.ExecuteNonQuery();
        var job = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Assert.Equal(750, FinanceReleaseStore.Decision(c, null, job).Balance);
        Assert.Throws<ArgumentException>(() => FinanceReleaseStore.Require(c, null, job));
        Execute("insert into studio_finance_authorizations values(gen_random_uuid(),'11111111-1111-1111-1111-111111111111',null,'Client','FinalDelivery',750,null,now())");
        FinanceReleaseStore.Require(c, null, job);
        Assert.Equal(750, FinanceReleaseStore.Balance(c, null, job));
        Execute("insert into studio_financial_events values('11111111-1111-1111-1111-111111111111','Refund',-50)");
        Assert.False(FinanceReleaseStore.Decision(c, null, job).Authorized);
        Execute("update studio_finance_authorizations set authorized_amount=800");
        Assert.True(FinanceReleaseStore.Decision(c, null, job).Authorized);
        Execute("update studio_jobs set client_name='Different client'");
        Assert.False(FinanceReleaseStore.Decision(c, null, job).Authorized);
        Execute("update studio_jobs set client_name='Client'; update studio_finance_authorizations set revoked_at_utc=now()");
        Assert.False(FinanceReleaseStore.Decision(c, null, job).Authorized);
        Execute("insert into studio_financial_events values('11111111-1111-1111-1111-111111111111','Payment',800)");
        Assert.True(FinanceReleaseStore.Decision(c, null, job).Authorized);
        void Execute(string sql) { using var q = new NpgsqlCommand(sql, c); q.ExecuteNonQuery(); }
    }

    [StudioPostgresFact]
    public void AnInvoiceCannotBeReleasedByMarkingItPaidOrLeavingTheJobLedgerEmpty()
    {
        using var c = new NpgsqlConnection(Environment.GetEnvironmentVariable("STUDIO_TEST_POSTGRES"));
        c.Open();
        using var q = new NpgsqlCommand("""
            create temp table studio_financial_events(job_id uuid,kind text,amount numeric);
            create temp table studio_account_invoices(id uuid,status text,currency text,total_amount numeric);
            create temp table studio_account_invoice_jobs(invoice_id uuid,job_id uuid);
            insert into studio_account_invoices values('22222222-2222-2222-2222-222222222222','Paid','JMD',500);
            insert into studio_account_invoice_jobs values('22222222-2222-2222-2222-222222222222','11111111-1111-1111-1111-111111111111'),('22222222-2222-2222-2222-222222222222','33333333-3333-3333-3333-333333333333');
            insert into studio_financial_events values('33333333-3333-3333-3333-333333333333','Payment',200);
            """, c);
        q.ExecuteNonQuery();
        Assert.Equal(300, FinanceReleaseStore.Balance(c, null, Guid.Parse("11111111-1111-1111-1111-111111111111")));
    }
    [StudioPostgresFact]
    public void ReceiptsAreNotCountedAgainForEveryConnectedInvoice()
    {
        using var c=new NpgsqlConnection(Environment.GetEnvironmentVariable("STUDIO_TEST_POSTGRES"));c.Open();
        using var q=new NpgsqlCommand("""
            create temp table studio_financial_events(job_id uuid,kind text,amount numeric);
            create temp table studio_account_invoices(id uuid,status text,currency text,total_amount numeric);
            create temp table studio_account_invoice_jobs(invoice_id uuid,job_id uuid);
            insert into studio_account_invoices values('22222222-2222-2222-2222-222222222222','Paid','JMD',100),('44444444-4444-4444-4444-444444444444','Issued','JMD',200);
            insert into studio_account_invoice_jobs values('22222222-2222-2222-2222-222222222222','11111111-1111-1111-1111-111111111111'),('44444444-4444-4444-4444-444444444444','11111111-1111-1111-1111-111111111111'),('44444444-4444-4444-4444-444444444444','33333333-3333-3333-3333-333333333333');
            insert into studio_financial_events values('33333333-3333-3333-3333-333333333333','Payment',150);
            """,c);q.ExecuteNonQuery();
        Assert.Equal(150,FinanceReleaseStore.Balance(c,null,Guid.Parse("11111111-1111-1111-1111-111111111111")));
        Assert.Equal(150,FinanceReleaseStore.Balance(c,null,Guid.Parse("33333333-3333-3333-3333-333333333333")));
    }

}
