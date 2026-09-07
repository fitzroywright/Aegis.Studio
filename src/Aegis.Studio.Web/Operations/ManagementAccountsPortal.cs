using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Aegis.Studio.Web.Notifications;
using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Operations;

public static class ManagementAccountsPortalEndpoints
{
    public static void MapStudioManagementAccountsPortal(this WebApplication app)
    {
        StudioDatabase database = app.Services.GetRequiredService<StudioDatabase>();
        StudioManagementStore management = new(database);
        StudioAccountsStore accounts = new(database);
        ClientPortalStore portal = new(database);
        GlobalOperationalRoleStore globalRoles = new(database);
        accounts.EnsureSchema();
        portal.EnsureSchema();
        globalRoles.EnsureSchema();

        app.MapGet("/api/management/dashboard", (HttpContext context) =>
        {
            if (!context.User.IsInRole("Administrator")) return Results.Forbid();
            return Results.Ok(management.GetDashboard());
        }).RequireAuthorization();

        app.MapGet("/api/management/jobs-by-status", (HttpContext context) =>
        {
            if (!context.User.IsInRole("Administrator")) return Results.Forbid();
            return Results.Ok(management.GetJobsByStatus());
        }).RequireAuthorization();

        app.MapGet("/api/management/revenue-by-month", (HttpContext context, int? months) =>
        {
            if (!context.User.IsInRole("Administrator")) return Results.Forbid();
            return Results.Ok(management.GetRevenueByMonth(Math.Clamp(months ?? 12, 1, 36)));
        }).RequireAuthorization();

        app.MapGet("/api/accounts/invoices", (HttpContext context, JobAccessService jobAccess) =>
        {
            IReadOnlyList<AccountInvoice> values = accounts.GetInvoices();
            if (context.User.IsInRole("Administrator") || globalRoles.HasPermission(context.User, "Billing")) return Results.Ok(values);
            HashSet<Guid> allowed = jobAccess.GetAccessibleJobIds(context.User).Where(id => jobAccess.HasPermission(context.User, id, "ViewFinancials")).ToHashSet();
            return Results.Ok(values.Where(x => allowed.Contains(x.JobId)));
        }).RequireAuthorization();

        app.MapGet("/api/jobs/{jobId:guid}/invoices", (Guid jobId, HttpContext context, JobAccessService jobAccess) =>
        {
            if (!context.User.IsInRole("Administrator") && !globalRoles.HasPermission(context.User, "Billing") && !jobAccess.HasPermission(context.User, jobId, "ViewFinancials")) return Results.Forbid();
            return Results.Ok(accounts.GetInvoices(jobId));
        }).RequireAuthorization();

        app.MapPost("/api/jobs/{jobId:guid}/invoices", (Guid jobId, InvoiceRequest request, HttpContext context, JobAccessService jobAccess) =>
        {
            if (!context.User.IsInRole("Administrator") && !globalRoles.HasPermission(context.User, "Billing") && !jobAccess.HasPermission(context.User, jobId, "RecordFinancialActivity")) return Results.Forbid();
            try { return Results.Ok(accounts.CreateInvoice(jobId, request, UserName(context.User))); }
            catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        }).RequireAuthorization();

        app.MapPut("/api/accounts/invoices/{invoiceId:guid}/status", (Guid invoiceId, InvoiceStatusRequest request, HttpContext context) =>
        {
            if (!context.User.IsInRole("Administrator") && !globalRoles.HasPermission(context.User, "Billing")) return Results.Forbid();
            try { return Results.Ok(accounts.SetStatus(invoiceId, request.Status)); }
            catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        }).RequireAuthorization();

        app.MapPost("/api/jobs/{jobId:guid}/client-portal", (Guid jobId, ClientPortalAccessRequest request, HttpContext context, JobAccessService jobAccess) =>
        {
            if (!context.User.IsInRole("Administrator") && !globalRoles.HasPermission(context.User, "ReplyToClients") && !jobAccess.HasPermission(context.User, jobId, "ReplyRequester")) return Results.Forbid();
            try
            {
                ClientPortalIssueResult result = portal.Issue(jobId, request.ExpiresInDays ?? 30, UserName(context.User));
                string publicUrl = $"{context.Request.Scheme}://{context.Request.Host}/client/{result.Token}";
                return Results.Ok(new { result.Access, publicUrl, token = result.Token });
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        }).RequireAuthorization();

        app.MapDelete("/api/jobs/{jobId:guid}/client-portal", (Guid jobId, HttpContext context, JobAccessService jobAccess) =>
        {
            if (!context.User.IsInRole("Administrator") && !jobAccess.HasPermission(context.User, jobId, "AdminJob")) return Results.Forbid();
            portal.Revoke(jobId);
            return Results.NoContent();
        }).RequireAuthorization();

        app.MapGet("/client/{token}", (string token, IWebHostEnvironment environment) => portal.Validate(token) is null
            ? Results.NotFound()
            : Results.File(Path.Combine(environment.WebRootPath, "client-portal.html"), "text/html; charset=utf-8")).AllowAnonymous();

        app.MapGet("/client/{token}/data", (string token) =>
        {
            ClientPortalAccess? access = portal.Validate(token);
            if (access is null) return Results.Unauthorized();
            return Results.Ok(portal.GetData(access.JobId, accounts));
        }).AllowAnonymous();

        app.MapPost("/client/{token}/messages", (string token, ClientPortalMessageRequest request, NotificationService notifications) =>
        {
            ClientPortalAccess? access = portal.Validate(token);
            if (access is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(request.Body)) return Results.BadRequest(new { error = "Message is required." });
            ClientPortalData data = portal.GetData(access.JobId, accounts);
            portal.AddClientMessage(access.JobId, request.Body.Trim());
            notifications.PublishToAll("ClientPortalMessage", $"Client message: {data.JobTitle}", request.Body.Trim(), "Job", access.JobId.ToString("D"));
            return Results.Ok(new { sent = true });
        }).AllowAnonymous();
    }

    private static string UserName(ClaimsPrincipal user) => user.FindFirstValue("login_name") ?? user.Identity?.Name ?? "SYSTEM";
}

public sealed class StudioManagementStore
{
    private readonly StudioDatabase database;
    public StudioManagementStore(StudioDatabase database) => this.database = database;

    public ManagementDashboard GetDashboard()
    {
        using NpgsqlConnection connection = database.OpenConnection();
        long openConversations = ScalarLong(connection, "select count(*) from studio_conversations where not is_closed");
        long totalJobs = ScalarLong(connection, "select count(*) from studio_jobs");
        long activeJobs = ScalarLong(connection, "select count(*) from studio_jobs where lower(status)<>'completed'");
        long overdueActivities = ScalarLong(connection, "select count(*) from studio_job_activities where status<>'Completed' and due_at_utc is not null and due_at_utc<now()");
        long blockedActivities = ScalarLong(connection, "select count(*) from studio_job_activities where status='Blocked'");
        long failedWorkflows = ScalarLong(connection, "select count(*) from studio_workflow_runs where status='Failed'");
        long waitingWorkflows = ScalarLong(connection, "select count(*) from studio_workflow_runs where status='Waiting'");
        long publishedGalleries = ScalarLong(connection, "select count(*) from studio_workspace_galleries where is_published");
        decimal recordedRevenue = ScalarDecimal(connection, "select coalesce(sum(amount),0) from studio_financial_events");
        decimal outstandingInvoices = ScalarDecimal(connection, "select coalesce(sum(total_amount),0) from studio_account_invoices where status in ('Issued','Overdue')");
        long overdueInvoices = ScalarLong(connection, "select count(*) from studio_account_invoices where status in ('Issued','Overdue') and due_at_utc<now()");
        return new ManagementDashboard(openConversations,totalJobs,activeJobs,overdueActivities,blockedActivities,failedWorkflows,waitingWorkflows,publishedGalleries,recordedRevenue,outstandingInvoices,overdueInvoices,DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<DashboardBucket> GetJobsByStatus()
    {
        using NpgsqlConnection connection=database.OpenConnection(); using NpgsqlCommand command=connection.CreateCommand(); command.CommandText="select status,count(*) from studio_jobs group by status order by count(*) desc,status"; using NpgsqlDataReader reader=command.ExecuteReader(); List<DashboardBucket> values=[]; while(reader.Read()) values.Add(new(reader.GetString(0),reader.GetInt64(1),0)); return values;
    }

    public IReadOnlyList<DashboardBucket> GetRevenueByMonth(int months)
    {
        using NpgsqlConnection connection=database.OpenConnection(); using NpgsqlCommand command=connection.CreateCommand(); command.CommandText="select to_char(date_trunc('month',created_at_utc),'YYYY-MM'),count(*),coalesce(sum(amount),0) from studio_financial_events where created_at_utc>=date_trunc('month',now())-($1::int-1)*interval '1 month' group by 1 order by 1";command.Parameters.AddWithValue(months);using NpgsqlDataReader reader=command.ExecuteReader();List<DashboardBucket> values=[];while(reader.Read())values.Add(new(reader.GetString(0),reader.GetInt64(1),reader.GetDecimal(2)));return values;
    }

    private static long ScalarLong(NpgsqlConnection connection,string sql){using NpgsqlCommand command=connection.CreateCommand();command.CommandText=sql;return Convert.ToInt64(command.ExecuteScalar());}
    private static decimal ScalarDecimal(NpgsqlConnection connection,string sql){using NpgsqlCommand command=connection.CreateCommand();command.CommandText=sql;return Convert.ToDecimal(command.ExecuteScalar());}
}

public sealed class StudioAccountsStore
{
    private readonly StudioDatabase database;
    public StudioAccountsStore(StudioDatabase database)=>this.database=database;

    public void EnsureSchema()
    {
        using NpgsqlConnection connection=database.OpenConnection();using NpgsqlCommand command=connection.CreateCommand();command.CommandText="""
            create table if not exists studio_account_invoices
            (
                id uuid primary key,
                invoice_number text not null unique,
                job_id uuid not null references studio_jobs(id) on delete cascade,
                client_name text not null,
                client_email text null,
                currency text not null,
                subtotal numeric(18,2) not null,
                tax_amount numeric(18,2) not null,
                total_amount numeric(18,2) not null,
                status text not null,
                due_at_utc timestamptz not null,
                issued_at_utc timestamptz null,
                paid_at_utc timestamptz null,
                created_by text not null,
                created_at_utc timestamptz not null,
                updated_at_utc timestamptz not null
            );
            create index if not exists ix_studio_account_invoices_job on studio_account_invoices(job_id,created_at_utc desc);
            create index if not exists ix_studio_account_invoices_due on studio_account_invoices(status,due_at_utc);
            create table if not exists studio_account_invoice_lines
            (
                id uuid primary key,
                invoice_id uuid not null references studio_account_invoices(id) on delete cascade,
                description text not null,
                quantity integer not null,
                unit_price numeric(18,2) not null,
                line_total numeric(18,2) not null,
                sort_order integer not null
            );
            """;command.ExecuteNonQuery();
    }

    public AccountInvoice CreateInvoice(Guid jobId,InvoiceRequest request,string userName)
    {
        if(request.Lines is null||request.Lines.Count==0)throw new InvalidOperationException("At least one invoice line is required.");
        foreach(InvoiceLineRequest line in request.Lines) if(string.IsNullOrWhiteSpace(line.Description)||line.Quantity<=0||line.UnitPrice<0) throw new InvalidOperationException("Invoice lines require description, positive quantity and non-negative price.");
        using NpgsqlConnection connection=database.OpenConnection();string clientName;string? clientEmail;
        using(NpgsqlCommand lookup=connection.CreateCommand()){lookup.CommandText="select j.client_name,c.contact_address from studio_jobs j join studio_conversations c on c.id=j.conversation_id where j.id=$1";lookup.Parameters.AddWithValue(jobId);using NpgsqlDataReader reader=lookup.ExecuteReader();if(!reader.Read())throw new KeyNotFoundException();clientName=reader.GetString(0);clientEmail=reader.GetString(1).Contains('@')?reader.GetString(1):null;}
        decimal subtotal=request.Lines.Sum(x=>x.UnitPrice*x.Quantity);decimal tax=Math.Round(subtotal*Math.Max(0,request.TaxRatePercent)/100m,2);decimal total=subtotal+tax;Guid id=Guid.NewGuid();string number=NextInvoiceNumber(connection);
        using NpgsqlTransaction transaction=connection.BeginTransaction();
        using(NpgsqlCommand insert=connection.CreateCommand()){insert.Transaction=transaction;insert.CommandText="insert into studio_account_invoices(id,invoice_number,job_id,client_name,client_email,currency,subtotal,tax_amount,total_amount,status,due_at_utc,issued_at_utc,paid_at_utc,created_by,created_at_utc,updated_at_utc) values($1,$2,$3,$4,$5,$6,$7,$8,$9,'Draft',$10,null,null,$11,now(),now())";object?[] values=[id,number,jobId,clientName,(object?)clientEmail??DBNull.Value,string.IsNullOrWhiteSpace(request.Currency)?"JMD":request.Currency.Trim().ToUpperInvariant(),subtotal,tax,total,request.DueAtUtc.ToUniversalTime(),userName];foreach(object? value in values)insert.Parameters.AddWithValue(value??DBNull.Value);insert.ExecuteNonQuery();}
        for(int i=0;i<request.Lines.Count;i++){InvoiceLineRequest line=request.Lines[i];using NpgsqlCommand insert=connection.CreateCommand();insert.Transaction=transaction;insert.CommandText="insert into studio_account_invoice_lines(id,invoice_id,description,quantity,unit_price,line_total,sort_order) values($1,$2,$3,$4,$5,$6,$7)";insert.Parameters.AddWithValue(Guid.NewGuid());insert.Parameters.AddWithValue(id);insert.Parameters.AddWithValue(line.Description.Trim());insert.Parameters.AddWithValue(line.Quantity);insert.Parameters.AddWithValue(line.UnitPrice);insert.Parameters.AddWithValue(line.UnitPrice*line.Quantity);insert.Parameters.AddWithValue(i);insert.ExecuteNonQuery();}
        transaction.Commit();return GetInvoices().First(x=>x.Id==id);
    }

    public AccountInvoice SetStatus(Guid id,string status)
    {
        string normalized=status?.Trim()??string.Empty;if(normalized is not ("Draft" or "Issued" or "Paid" or "Void" or "Overdue"))throw new InvalidOperationException("Invalid invoice status.");
        using NpgsqlConnection connection=database.OpenConnection();using NpgsqlCommand command=connection.CreateCommand();command.CommandText="update studio_account_invoices set status=$2,issued_at_utc=case when $2='Issued' then coalesce(issued_at_utc,now()) else issued_at_utc end,paid_at_utc=case when $2='Paid' then coalesce(paid_at_utc,now()) else paid_at_utc end,updated_at_utc=now() where id=$1";command.Parameters.AddWithValue(id);command.Parameters.AddWithValue(normalized);if(command.ExecuteNonQuery()!=1)throw new KeyNotFoundException();return GetInvoices().First(x=>x.Id==id);
    }

    public IReadOnlyList<AccountInvoice> GetInvoices(Guid? jobId=null)
    {
        using NpgsqlConnection connection=database.OpenConnection();using NpgsqlCommand command=connection.CreateCommand();command.CommandText="select id,invoice_number,job_id,client_name,client_email,currency,subtotal,tax_amount,total_amount,status,due_at_utc,issued_at_utc,paid_at_utc,created_by,created_at_utc,updated_at_utc from studio_account_invoices where ($1::uuid is null or job_id=$1) order by created_at_utc desc";command.Parameters.AddWithValue((object?)jobId??DBNull.Value);using NpgsqlDataReader reader=command.ExecuteReader();List<AccountInvoice> values=[];while(reader.Read()){Guid id=reader.GetGuid(0);values.Add(new(id,reader.GetString(1),reader.GetGuid(2),reader.GetString(3),reader.IsDBNull(4)?null:reader.GetString(4),reader.GetString(5),reader.GetDecimal(6),reader.GetDecimal(7),reader.GetDecimal(8),reader.GetString(9),reader.GetFieldValue<DateTimeOffset>(10),reader.IsDBNull(11)?null:reader.GetFieldValue<DateTimeOffset>(11),reader.IsDBNull(12)?null:reader.GetFieldValue<DateTimeOffset>(12),reader.GetString(13),reader.GetFieldValue<DateTimeOffset>(14),reader.GetFieldValue<DateTimeOffset>(15),[]));}reader.Close();return values.Select(x=>x with{Lines=GetLines(connection,x.Id)}).ToList();
    }

    private IReadOnlyList<AccountInvoiceLine> GetLines(NpgsqlConnection connection,Guid invoiceId){using NpgsqlCommand command=connection.CreateCommand();command.CommandText="select id,description,quantity,unit_price,line_total,sort_order from studio_account_invoice_lines where invoice_id=$1 order by sort_order";command.Parameters.AddWithValue(invoiceId);using NpgsqlDataReader reader=command.ExecuteReader();List<AccountInvoiceLine> values=[];while(reader.Read())values.Add(new(reader.GetGuid(0),reader.GetString(1),reader.GetInt32(2),reader.GetDecimal(3),reader.GetDecimal(4),reader.GetInt32(5)));return values;}
    private static string NextInvoiceNumber(NpgsqlConnection connection){using NpgsqlCommand command=connection.CreateCommand();command.CommandText="select coalesce(max(cast(substring(invoice_number from '[0-9]+$') as integer)),0)+1 from studio_account_invoices where invoice_number like 'INV-%'";int next=Convert.ToInt32(command.ExecuteScalar());return $"INV-{DateTime.UtcNow:yyyy}-{next:00000}";}
}

public sealed class ClientPortalStore
{
    private readonly StudioDatabase database;
    public ClientPortalStore(StudioDatabase database)=>this.database=database;
    public void EnsureSchema(){using NpgsqlConnection c=database.OpenConnection();using NpgsqlCommand q=c.CreateCommand();q.CommandText="""
        create table if not exists studio_client_portal_access
        (
            id uuid primary key,
            job_id uuid not null unique references studio_jobs(id) on delete cascade,
            token_hash text not null unique,
            expires_at_utc timestamptz not null,
            revoked_at_utc timestamptz null,
            created_by text not null,
            created_at_utc timestamptz not null,
            last_accessed_at_utc timestamptz null
        );
        """;q.ExecuteNonQuery();}
    public ClientPortalIssueResult Issue(Guid jobId,int days,string userName){using NpgsqlConnection c=database.OpenConnection();using NpgsqlCommand exists=c.CreateCommand();exists.CommandText="select exists(select 1 from studio_jobs where id=$1)";exists.Parameters.AddWithValue(jobId);if(exists.ExecuteScalar() is not bool found||!found)throw new KeyNotFoundException();string token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();DateTimeOffset expires=DateTimeOffset.UtcNow.AddDays(Math.Clamp(days,1,365));using NpgsqlCommand q=c.CreateCommand();q.CommandText="insert into studio_client_portal_access(id,job_id,token_hash,expires_at_utc,revoked_at_utc,created_by,created_at_utc) values($1,$2,$3,$4,null,$5,now()) on conflict(job_id) do update set token_hash=excluded.token_hash,expires_at_utc=excluded.expires_at_utc,revoked_at_utc=null,created_by=excluded.created_by,created_at_utc=now() returning id";Guid newId=Guid.NewGuid();q.Parameters.AddWithValue(newId);q.Parameters.AddWithValue(jobId);q.Parameters.AddWithValue(Hash(token));q.Parameters.AddWithValue(expires);q.Parameters.AddWithValue(userName);Guid actualId=(Guid)(q.ExecuteScalar()??newId);return new ClientPortalIssueResult(new ClientPortalAccess(actualId,jobId,expires,null,userName,DateTimeOffset.UtcNow),token);}
    public ClientPortalAccess? Validate(string token){if(string.IsNullOrWhiteSpace(token))return null;using NpgsqlConnection c=database.OpenConnection();using NpgsqlCommand q=c.CreateCommand();q.CommandText="select id,job_id,expires_at_utc,revoked_at_utc,created_by,created_at_utc from studio_client_portal_access where token_hash=$1 and revoked_at_utc is null and expires_at_utc>now()";q.Parameters.AddWithValue(Hash(token));using NpgsqlDataReader r=q.ExecuteReader();if(!r.Read())return null;ClientPortalAccess value=new(r.GetGuid(0),r.GetGuid(1),r.GetFieldValue<DateTimeOffset>(2),r.IsDBNull(3)?null:r.GetFieldValue<DateTimeOffset>(3),r.GetString(4),r.GetFieldValue<DateTimeOffset>(5));r.Close();using NpgsqlCommand touch=c.CreateCommand();touch.CommandText="update studio_client_portal_access set last_accessed_at_utc=now() where id=$1";touch.Parameters.AddWithValue(value.Id);touch.ExecuteNonQuery();return value;}
    public void Revoke(Guid jobId){using NpgsqlConnection c=database.OpenConnection();using NpgsqlCommand q=c.CreateCommand();q.CommandText="update studio_client_portal_access set revoked_at_utc=now() where job_id=$1";q.Parameters.AddWithValue(jobId);q.ExecuteNonQuery();}
    public ClientPortalData GetData(Guid jobId,StudioAccountsStore accounts){using NpgsqlConnection c=database.OpenConnection();using NpgsqlCommand q=c.CreateCommand();q.CommandText="select j.title,j.client_name,j.status,j.event_date,c.id,c.subject,c.contact_address from studio_jobs j join studio_conversations c on c.id=j.conversation_id where j.id=$1";q.Parameters.AddWithValue(jobId);using NpgsqlDataReader r=q.ExecuteReader();if(!r.Read())throw new KeyNotFoundException();string title=r.GetString(0),client=r.GetString(1),status=r.GetString(2);DateOnly? eventDate=r.IsDBNull(3)?null:r.GetFieldValue<DateOnly>(3);Guid conversationId=r.GetGuid(4);string subject=r.GetString(5);string contact=r.GetString(6);r.Close();using NpgsqlCommand messages=c.CreateCommand();messages.CommandText="select author_display_name,visibility,body,created_at_utc from studio_messages where conversation_id=$1 and visibility='External' order by created_at_utc";messages.Parameters.AddWithValue(conversationId);using NpgsqlDataReader mr=messages.ExecuteReader();List<ClientPortalMessage> list=[];while(mr.Read())list.Add(new(mr.GetString(0),mr.GetString(1),mr.GetString(2),mr.GetFieldValue<DateTimeOffset>(3)));mr.Close();return new ClientPortalData(jobId,title,client,status,eventDate,subject,contact,accounts.GetInvoices(jobId),list);}
    public void AddClientMessage(Guid jobId,string body){using NpgsqlConnection c=database.OpenConnection();using NpgsqlCommand q=c.CreateCommand();q.CommandText="select conversation_id,client_name from studio_jobs where id=$1";q.Parameters.AddWithValue(jobId);using NpgsqlDataReader r=q.ExecuteReader();if(!r.Read())throw new KeyNotFoundException();Guid conversationId=r.GetGuid(0);string client=r.GetString(1);r.Close();using NpgsqlCommand insert=c.CreateCommand();insert.CommandText="insert into studio_messages(id,conversation_id,author_user_name,author_display_name,visibility,body,created_at_utc,external_message_id) values($1,$2,null,$3,'External',$4,now(),null)";insert.Parameters.AddWithValue(Guid.NewGuid());insert.Parameters.AddWithValue(conversationId);insert.Parameters.AddWithValue(client);insert.Parameters.AddWithValue(body);insert.ExecuteNonQuery();}
    private static string Hash(string token)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}

public sealed record ManagementDashboard(long OpenConversations,long TotalJobs,long ActiveJobs,long OverdueActivities,long BlockedActivities,long FailedWorkflows,long WaitingWorkflows,long PublishedGalleries,decimal RecordedRevenue,decimal OutstandingInvoices,long OverdueInvoices,DateTimeOffset GeneratedAtUtc);
public sealed record DashboardBucket(string Label,long Count,decimal Amount);
public sealed record InvoiceLineRequest(string Description,int Quantity,decimal UnitPrice);
public sealed record InvoiceRequest(IReadOnlyList<InvoiceLineRequest> Lines,decimal TaxRatePercent,DateTimeOffset DueAtUtc,string Currency="JMD");
public sealed record InvoiceStatusRequest(string Status);
public sealed record AccountInvoiceLine(Guid Id,string Description,int Quantity,decimal UnitPrice,decimal LineTotal,int SortOrder);
public sealed record AccountInvoice(Guid Id,string InvoiceNumber,Guid JobId,string ClientName,string? ClientEmail,string Currency,decimal Subtotal,decimal TaxAmount,decimal TotalAmount,string Status,DateTimeOffset DueAtUtc,DateTimeOffset? IssuedAtUtc,DateTimeOffset? PaidAtUtc,string CreatedBy,DateTimeOffset CreatedAtUtc,DateTimeOffset UpdatedAtUtc,IReadOnlyList<AccountInvoiceLine> Lines);
public sealed record ClientPortalAccessRequest(int? ExpiresInDays);
public sealed record ClientPortalAccess(Guid Id,Guid JobId,DateTimeOffset ExpiresAtUtc,DateTimeOffset? RevokedAtUtc,string CreatedBy,DateTimeOffset CreatedAtUtc);
public sealed record ClientPortalIssueResult(ClientPortalAccess Access,string Token);
public sealed record ClientPortalMessageRequest(string Body);
public sealed record ClientPortalMessage(string Author,string Visibility,string Body,DateTimeOffset CreatedAtUtc);
public sealed record ClientPortalData(Guid JobId,string JobTitle,string ClientName,string Status,DateOnly? EventDate,string Subject,string ContactAddress,IReadOnlyList<AccountInvoice> Invoices,IReadOnlyList<ClientPortalMessage> Messages);
