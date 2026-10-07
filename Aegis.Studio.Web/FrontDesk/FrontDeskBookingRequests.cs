using System.Security.Claims;
using System.Text.Json;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.FrontDesk;

public sealed record PendingBookingRequest(Guid WorkspaceId,string ClientName,string ClientEmail,string Title,DateOnly EventDate,decimal RequiredDeposit,string Currency,string? Notes,Guid? ConversationId=null,Guid? ClientId=null,string? ExternalBookingId=null,DateTimeOffset? StartsAtUtc=null,DateTimeOffset? EndsAtUtc=null,string? Location=null,string? ClientPhone=null);
public sealed record VerifiedDepositRequest(decimal Amount,string Currency,string Provider,string Reference);
public sealed record FrontDeskRequest(Guid Id,PendingBookingRequest Request,string Status,Guid? JobId,decimal RecordedDeposit,DateTimeOffset CreatedAtUtc);

public static class FrontDeskRequestRules
{
    public static void Validate(PendingBookingRequest request)
    {
        if(request.WorkspaceId==Guid.Empty || string.IsNullOrWhiteSpace(request.Title) || request.Title.Length>200 || string.IsNullOrWhiteSpace(request.ClientName) || request.ClientName.Length>200)
            throw new ArgumentException("Workspace, customer and job title are required (maximum 200 characters).");
        if(!System.Net.Mail.MailAddress.TryCreate(request.ClientEmail,out _) || request.ClientEmail.Length>320) throw new ArgumentException("A valid customer email is required.");
        if(request.EventDate==default || (request.Notes?.Length ?? 0)>4000) throw new ArgumentException("Event date is required and notes must be at most 4000 characters.");
        if((request.ExternalBookingId?.Length??0)>200 || (request.Location?.Length??0)>500 || (request.ClientPhone?.Length??0)>80)throw new ArgumentException("Booking reference or location is too long.");
        if(request.StartsAtUtc.HasValue != request.EndsAtUtc.HasValue || (request.StartsAtUtc.HasValue && request.EndsAtUtc<=request.StartsAtUtc))throw new ArgumentException("Choose a valid booking time range.");
        ValidateMoney(request.RequiredDeposit,request.Currency);
        if(!string.Equals(request.Currency.Trim(),"JMD",StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The current Studio deposit ledger supports JMD only.");
    }
    public static void ValidateMoney(decimal amount,string currency)
    {
        if(amount<=0 || amount>999999999 || decimal.Round(amount,2)!=amount) throw new ArgumentException("Amount must be positive, at most 999999999, and have at most two decimal places.");
        if(string.IsNullOrWhiteSpace(currency) || currency.Trim().Length!=3 || !currency.Trim().All(char.IsAsciiLetter)) throw new ArgumentException("A three-letter currency is required.");
    }
    public static bool DepositSatisfied(decimal received,decimal required)=>required>0 && received>=required;
}

public sealed class FrontDeskBookingRequestStore(StudioDatabase database)
{
    private static readonly JsonSerializerOptions JsonOptions=new(JsonSerializerDefaults.Web);
    public void EnsureSchema()
    {
        string[] sql=[
            "create table if not exists studio_frontdesk_requests(id uuid primary key,workspace_id uuid not null references studio_workspaces(id),payload jsonb not null,status text not null default 'Pending',job_id uuid null references studio_jobs(id),created_by text not null,created_at_utc timestamptz not null default now(),deposit_requested_at_utc timestamptz null)",
            "create table if not exists studio_frontdesk_deposits(id uuid primary key,request_id uuid not null references studio_frontdesk_requests(id),amount numeric(18,2) not null check(amount>0),currency text not null,provider text not null,reference text not null,recorded_by text not null,created_at_utc timestamptz not null default now(),unique(provider,reference))",
            "create index if not exists ix_frontdesk_request_deposits on studio_frontdesk_deposits(request_id)",
            "create unique index if not exists ux_frontdesk_external_booking on studio_frontdesk_requests((payload->>'externalBookingId')) where payload->>'externalBookingId' is not null"
        ];
        using var connection=database.OpenConnection();using var tx=connection.BeginTransaction();
        foreach(string statement in sql)Execute(connection,tx,statement);
        tx.Commit();
    }
    public IReadOnlyList<FrontDeskRequest> GetAll()
    {
        using var connection=database.OpenConnection();using var command=new NpgsqlCommand("select r.id,r.payload::text,r.status,r.job_id,coalesce((select sum(d.amount) from studio_frontdesk_deposits d where d.request_id=r.id),0),r.created_at_utc from studio_frontdesk_requests r order by r.created_at_utc desc",connection);
        using var reader=command.ExecuteReader();List<FrontDeskRequest> result=[];
        while(reader.Read())result.Add(Read(reader));return result;
    }
    public FrontDeskRequest Create(PendingBookingRequest request,string user)
    {
        FrontDeskRequestRules.Validate(request);
        request=request with{ClientName=request.ClientName.Trim(),ClientEmail=request.ClientEmail.Trim(),Title=request.Title.Trim(),Currency=request.Currency.Trim().ToUpperInvariant(),ExternalBookingId=string.IsNullOrWhiteSpace(request.ExternalBookingId)?null:request.ExternalBookingId.Trim()};
        using var connection=database.OpenConnection();using var tx=connection.BeginTransaction();
        if(request.ExternalBookingId is not null)
        {
            Execute(connection,tx,"select pg_advisory_xact_lock(hashtextextended($1,0))","booking:"+request.ExternalBookingId);
            using var existing=new NpgsqlCommand("select r.id,r.payload::text,r.status,r.job_id,coalesce((select sum(d.amount) from studio_frontdesk_deposits d where d.request_id=r.id),0),r.created_at_utc from studio_frontdesk_requests r where payload->>'externalBookingId'=$1",connection,tx);
            existing.Parameters.AddWithValue(request.ExternalBookingId);using var reader=existing.ExecuteReader();
            if(reader.Read())
            {
                FrontDeskRequest prior=Read(reader);
                // The server's deposit policy can change between retries; compare the submitted booking facts.
                if((prior.Request with { RequiredDeposit=request.RequiredDeposit }) != request)throw new ArgumentException("This booking reference was already used with different details.");
                return prior;
            }
        }
        using(var workspace=new NpgsqlCommand("select id from studio_workspaces where id=$1 and is_enabled for share",connection,tx))
        {workspace.Parameters.AddWithValue(request.WorkspaceId);if(workspace.ExecuteScalar() is null)throw new ArgumentException("Choose an enabled workspace.");}
        if(request.ClientId.HasValue){using var client=new NpgsqlCommand("select id from studio_clients where id=$1",connection,tx);client.Parameters.AddWithValue(request.ClientId.Value);if(client.ExecuteScalar() is null)throw new ArgumentException("Client not found.");}
        if(request.ConversationId.HasValue)
        {
            using var source=new NpgsqlCommand("select id from studio_conversations where id=$1 and workspace_id=$2 and job_id is null for update",connection,tx);
            source.Parameters.AddWithValue(request.ConversationId.Value);source.Parameters.AddWithValue(request.WorkspaceId);
            if(source.ExecuteScalar() is null)throw new ArgumentException("This enquiry is missing, belongs to another workspace or already has a job.");
            using var duplicate=new NpgsqlCommand("select exists(select 1 from studio_frontdesk_requests where payload->>'conversationId'=$1)",connection,tx);
            duplicate.Parameters.AddWithValue(request.ConversationId.Value.ToString("D"));
            if((bool)duplicate.ExecuteScalar()!)throw new ArgumentException("A pending booking request already exists for this enquiry.");
        }
        Guid id=Guid.NewGuid();
        Execute(connection,tx,"insert into studio_frontdesk_requests(id,workspace_id,payload,created_by) values($1,$2,$3::jsonb,$4)",id,request.WorkspaceId,JsonSerializer.Serialize(request,JsonOptions),user);
        tx.Commit();return GetAll().Single(x=>x.Id==id);
    }
    public bool HasPendingConversation(Guid conversationId)
    {
        using var connection=database.OpenConnection();
        using var command=new NpgsqlCommand("select exists(select 1 from studio_frontdesk_requests where job_id is null and payload->>'conversationId'=$1)",connection);
        command.Parameters.AddWithValue(conversationId.ToString("D"));return (bool)command.ExecuteScalar()!;
    }
    public void MarkRequested(Guid id)
    {
        using var connection=database.OpenConnection();using var command=new NpgsqlCommand("update studio_frontdesk_requests set status='AwaitingDeposit',deposit_requested_at_utc=now() where id=$1 and status='Pending'",connection);
        command.Parameters.AddWithValue(id);command.ExecuteNonQuery();
    }
    public FrontDeskRequest RecordDeposit(Guid id,VerifiedDepositRequest payment,string user)
    {
        FrontDeskRequestRules.ValidateMoney(payment.Amount,payment.Currency);
        if(string.IsNullOrWhiteSpace(payment.Provider)||payment.Provider.Length>100||string.IsNullOrWhiteSpace(payment.Reference)||payment.Reference.Length>200)throw new ArgumentException("Provider and receipt/reference are required.");
        string provider=payment.Provider.Trim().ToLowerInvariant(),reference=payment.Reference.Trim().ToLowerInvariant(),currency=payment.Currency.Trim().ToUpperInvariant();
        using var connection=database.OpenConnection();using var tx=connection.BeginTransaction();
        // Serialize duplicate receipt checks across requests before locking the request.
        Execute(connection,tx,"select pg_advisory_xact_lock(hashtextextended($1,0))",provider+":"+reference);
        FrontDeskRequest current;
        using(var get=new NpgsqlCommand("select r.id,r.payload::text,r.status,r.job_id,coalesce((select sum(d.amount) from studio_frontdesk_deposits d where d.request_id=r.id),0),r.created_at_utc from studio_frontdesk_requests r where r.id=$1 for update",connection,tx))
        {get.Parameters.AddWithValue(id);using var reader=get.ExecuteReader();if(!reader.Read())throw new KeyNotFoundException("Booking request not found.");current=Read(reader);}
        // Read the total after acquiring the row lock, using a fresh READ COMMITTED snapshot.
        // A concurrent distinct receipt may have committed while this transaction waited.
        using(var total=new NpgsqlCommand("select coalesce(sum(amount),0) from studio_frontdesk_deposits where request_id=$1",connection,tx))
        {total.Parameters.AddWithValue(id);current=current with{RecordedDeposit=Convert.ToDecimal(total.ExecuteScalar())};}
        if(currency!=current.Request.Currency)throw new ArgumentException("Currency must match the booking request.");
        using(var prior=new NpgsqlCommand("select request_id,amount,currency from studio_frontdesk_deposits where provider=$1 and reference=$2",connection,tx))
        {
            prior.Parameters.AddWithValue(provider);prior.Parameters.AddWithValue(reference);using var reader=prior.ExecuteReader();
            if(reader.Read())
            {
                if(reader.GetGuid(0)!=id || reader.GetDecimal(1)!=payment.Amount || reader.GetString(2)!=currency)throw new ArgumentException("This payment reference was already recorded with different details.");
                return current;
            }
        }
        if(current.JobId.HasValue)throw new ArgumentException("The booking is confirmed. Record further payments through job Billing / POS.");
        Guid depositId=Guid.NewGuid();
        Execute(connection,tx,"insert into studio_frontdesk_deposits(id,request_id,amount,currency,provider,reference,recorded_by) values($1,$2,$3,$4,$5,$6,$7)",depositId,id,payment.Amount,currency,provider,reference,user);
        decimal received=current.RecordedDeposit+payment.Amount;
        if(FrontDeskRequestRules.DepositSatisfied(received,current.Request.RequiredDeposit))
        {
            var r=current.Request;Guid job=Guid.NewGuid(),conversation=r.ConversationId??Guid.NewGuid();
            if(r.ConversationId.HasValue)
            {
                using var enquiry=new NpgsqlCommand("select id from studio_conversations where id=$1 and workspace_id=$2 and job_id is null for update",connection,tx);
                enquiry.Parameters.AddWithValue(conversation);enquiry.Parameters.AddWithValue(r.WorkspaceId);
                if(enquiry.ExecuteScalar() is null)throw new ArgumentException("This enquiry already has a job. Resolve the existing job before recording this deposit.");
            }
            else Execute(connection,tx,"insert into studio_conversations(id,workspace_id,subject,contact_name,contact_address,channel,is_closed,created_at_utc) values($1,$2,$3,$4,$5,'Email',false,now())",conversation,r.WorkspaceId,r.Title,r.ClientName,r.ClientEmail);
            Execute(connection,tx,"insert into studio_jobs(id,workspace_id,conversation_id,title,client_name,status,event_date,created_at_utc,job_lead) values($1,$2,$3,$4,$5,'Needs Scheduling',$6,now(),(select default_job_lead from studio_workspaces where id=$2))",job,r.WorkspaceId,conversation,r.Title,r.ClientName,r.EventDate);
            Guid clientId=r.ClientId??Guid.Empty;
            if(clientId==Guid.Empty)
            {
                Execute(connection,tx,"select pg_advisory_xact_lock(hashtextextended($1,0))","client:"+r.ClientEmail.ToLowerInvariant()+":"+r.ClientName.ToLowerInvariant());
                using var client=new NpgsqlCommand("select id from studio_clients where lower(email)=lower($1) and lower(display_name)=lower($2) order by updated_at_utc desc limit 1",connection,tx);
                client.Parameters.AddWithValue(r.ClientEmail);client.Parameters.AddWithValue(r.ClientName);
                if(client.ExecuteScalar() is Guid existingClient)clientId=existingClient;
                else{clientId=Guid.NewGuid();Execute(connection,tx,"insert into studio_clients(id,display_name,email,phone) values($1,$2,$3,$4)",clientId,r.ClientName,r.ClientEmail,(object?)r.ClientPhone??DBNull.Value);}
            }
            Execute(connection,tx,"update studio_jobs set client_id=$2 where id=$1",job,clientId);
            if(r.StartsAtUtc.HasValue && r.EndsAtUtc.HasValue)
                Execute(connection,tx,"insert into studio_schedule_entries(id,job_id,booking_id,title,starts_at_utc,ends_at_utc,location,status,notes,created_at_utc) values($1,$2,null,$3,$4,$5,$6,'Requested',$7,now())",Guid.NewGuid(),job,r.Title,r.StartsAtUtc.Value.ToUniversalTime(),r.EndsAtUtc.Value.ToUniversalTime(),(object?)r.Location??DBNull.Value,(object?)r.Notes??DBNull.Value);
            Execute(connection,tx,"insert into studio_operational_audit(id,job_id,activity_id,action,user_name,detail,created_at_utc) values($1,$2,null,'BookingConfirmed',$3,'Deposit verified; job ready for scheduling.',now())",Guid.NewGuid(),job,user);
            Execute(connection,tx,"update studio_conversations set job_id=$2 where id=$1",conversation,job);
            Execute(connection,tx,"insert into studio_messages(id,conversation_id,author_display_name,visibility,body,created_at_utc) values($1,$2,'FrontDesk','Internal','Booking confirmed by FrontDesk. Job created and ready for manager scheduling.',now())",Guid.NewGuid(),conversation);
            Execute(connection,tx,"insert into studio_financial_events(id,job_id,kind,amount,note,created_at_utc) select id,$2,'Deposit',amount,currency||' / '||provider||' / '||reference,created_at_utc from studio_frontdesk_deposits where request_id=$1",id,job);
            Execute(connection,tx,"update studio_frontdesk_requests set status='Confirmed',job_id=$2 where id=$1",id,job);
        }
        else Execute(connection,tx,"update studio_frontdesk_requests set status='AwaitingDeposit' where id=$1",id);
        tx.Commit();return GetAll().Single(x=>x.Id==id);
    }
    private static FrontDeskRequest Read(NpgsqlDataReader r)=>new(r.GetGuid(0),JsonSerializer.Deserialize<PendingBookingRequest>(r.GetString(1),JsonOptions)!,r.GetString(2),r.IsDBNull(3)?null:r.GetGuid(3),r.GetDecimal(4),r.GetFieldValue<DateTimeOffset>(5));
    private static void Execute(NpgsqlConnection c,NpgsqlTransaction tx,string sql,params object[] args)
    {using var command=new NpgsqlCommand(sql,c,tx);foreach(object value in args)command.Parameters.AddWithValue(value);command.ExecuteNonQuery();}
}

public static class FrontDeskBookingRequestEndpoints
{
    public static void MapFrontDeskBookingRequests(this WebApplication app)
    {
        var group=app.MapGroup("/api/frontdesk").RequireAuthorization();
        group.MapGet("/booking-requests",(ClaimsPrincipal user,StudioDatabase db)=>Allowed(user,db)?Results.Ok(new FrontDeskBookingRequestStore(db).GetAll()):Results.Forbid());
        group.MapGet("/workspaces",(ClaimsPrincipal user,StudioDatabase db)=>
        {
            if(!Allowed(user,db))return Results.Forbid();
            using var connection=db.OpenConnection();using var command=new NpgsqlCommand("select id,name from studio_workspaces where is_enabled order by sort_order,name",connection);using var reader=command.ExecuteReader();var result=new List<object>();while(reader.Read())result.Add(new{id=reader.GetGuid(0),name=reader.GetString(1)});return Results.Ok(result);
        });
        group.MapGet("/enquiries",(ClaimsPrincipal user,StudioDatabase db)=>
        {
            if(!Allowed(user,db))return Results.Forbid();
            using var connection=db.OpenConnection();using var command=new NpgsqlCommand("select id,workspace_id,subject,contact_name,contact_address,channel,created_at_utc from studio_conversations where job_id is null and not is_closed order by created_at_utc desc",connection);using var reader=command.ExecuteReader();var result=new List<object>();while(reader.Read())result.Add(new{id=reader.GetGuid(0),workspaceId=reader.GetGuid(1),subject=reader.GetString(2),contactName=reader.GetString(3),contactAddress=reader.GetString(4),channel=reader.GetString(5),createdAtUtc=reader.GetFieldValue<DateTimeOffset>(6)});return Results.Ok(result);
        });
        group.MapPost("/booking-requests",(PendingBookingRequest request,ClaimsPrincipal user,StudioDatabase db)=>Allowed(user,db)?Run(()=>Results.Ok(new FrontDeskBookingRequestStore(db).Create(request,Name(user)))):Results.Forbid());
        group.MapPost("/booking-requests/{id:guid}/deposit-requested",(Guid id,ClaimsPrincipal user,StudioDatabase db)=>Allowed(user,db)?Run(()=>{new FrontDeskBookingRequestStore(db).MarkRequested(id);return Results.NoContent();}):Results.Forbid());
        group.MapPost("/booking-requests/{id:guid}/deposits",(Guid id,VerifiedDepositRequest request,ClaimsPrincipal user,StudioDatabase db)=>Allowed(user,db)?Run(()=>Results.Ok(new FrontDeskBookingRequestStore(db).RecordDeposit(id,request,Name(user)))):Results.Forbid());
    }
    private static bool Allowed(ClaimsPrincipal user,StudioDatabase db)=>user.IsInRole("Administrator") || (new GlobalOperationalRoleStore(db).HasPermission(user,"ViewAllJobs") && new GlobalOperationalRoleStore(db).HasPermission(user,"Billing"));
    private static string Name(ClaimsPrincipal user)=>user.FindFirstValue("login_name")??user.Identity?.Name??throw new UnauthorizedAccessException();
    private static IResult Run(Func<IResult> action)
    {try{return action();}catch(ArgumentException e){return Results.BadRequest(new{error=e.Message});}catch(KeyNotFoundException){return Results.NotFound();}}
}

