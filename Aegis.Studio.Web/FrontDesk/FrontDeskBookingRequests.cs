using System.Security.Claims;
using System.Text.Json;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Npgsql;
using Aegis.Studio.Web.Workflow;
using Aegis.Studio.Web.Production;
using Aegis.Studio.Web.Intake;

namespace Aegis.Studio.Web.FrontDesk;

public sealed record BookingPriceSnapshot(string SessionTypeId,string SessionType,long CatalogRevision,long PriceMinor,long DepositMinor,string Currency,IReadOnlyDictionary<string,int> ResourceRequirements,int SetupBufferMinutes=0,int CleanupBufferMinutes=0);
public sealed record PendingBookingRequest(Guid WorkspaceId,string ClientName,string ClientEmail,string Title,DateOnly EventDate,decimal RequiredDeposit,string Currency,string? Notes,Guid? ConversationId=null,Guid? ClientId=null,string? ExternalBookingId=null,DateTimeOffset? StartsAtUtc=null,DateTimeOffset? EndsAtUtc=null,string? Location=null,string? ClientPhone=null,string? Destination=null,string BookingSource="FrontDesk",Guid? JobTemplateId=null,Guid[]? SelectedServiceIds=null,BookingPriceSnapshot? PriceSnapshot=null);
public sealed record VerifiedDepositRequest(decimal Amount,string Currency,string Provider,string Reference);
public sealed record FrontDeskRequest(Guid Id,PendingBookingRequest Request,string Status,Guid? JobId,decimal RecordedDeposit,DateTimeOffset CreatedAtUtc,string? IntakeReference=null);

public static class FrontDeskRequestRules
{
    public static void Validate(PendingBookingRequest request)
    {
        if(request.WorkspaceId==Guid.Empty || string.IsNullOrWhiteSpace(request.Title) || request.Title.Length>200 || string.IsNullOrWhiteSpace(request.ClientName) || request.ClientName.Length>200)
            throw new ArgumentException("Workspace, client and job title are required (maximum 200 characters).");
        if(!System.Net.Mail.MailAddress.TryCreate(request.ClientEmail,out _) || request.ClientEmail.Length>320) throw new ArgumentException("A valid client email is required.");
        if(request.EventDate==default || (request.Notes?.Length ?? 0)>4000) throw new ArgumentException("Event date is required and notes must be at most 4000 characters.");
        if((request.ExternalBookingId?.Length??0)>200 || (request.Location?.Length??0)>500 || (request.ClientPhone?.Length??0)>80)throw new ArgumentException("Booking reference or location is too long.");
        if(request.StartsAtUtc.HasValue != request.EndsAtUtc.HasValue || (request.StartsAtUtc.HasValue && request.EndsAtUtc<=request.StartsAtUtc))throw new ArgumentException("Choose a valid booking time range.");
        if(request.RequiredDeposit<0 || request.RequiredDeposit>999999999 || decimal.Round(request.RequiredDeposit,2)!=request.RequiredDeposit)throw new ArgumentException("Required deposit metadata must be nonnegative.");
        if(!string.Equals(request.Currency.Trim(),"JMD",StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The current Studio deposit ledger supports JMD only.");
        if(request.PriceSnapshot is { } price && (string.IsNullOrWhiteSpace(price.SessionTypeId)||price.SessionTypeId.Length>100||string.IsNullOrWhiteSpace(price.SessionType)||price.SessionType.Length>160||price.CatalogRevision<1||price.PriceMinor<0||price.PriceMinor>99_999_999_900||price.DepositMinor<0||price.DepositMinor>price.PriceMinor||price.Currency!="JMD"||request.RequiredDeposit!=price.DepositMinor/100m||price.SetupBufferMinutes is <0 or >480||price.CleanupBufferMinutes is <0 or >480||price.ResourceRequirements is null||price.ResourceRequirements.Any(x=>string.IsNullOrWhiteSpace(x.Key)||x.Value is <0 or >20)))throw new ArgumentException("The published session price snapshot is invalid.");
    }
    public static void ValidateMoney(decimal amount,string currency)
    {
        if(amount<=0 || amount>999999999 || decimal.Round(amount,2)!=amount) throw new ArgumentException("Amount must be positive, at most 999999999, and have at most two decimal places.");
        if(string.IsNullOrWhiteSpace(currency) || currency.Trim().Length!=3 || !currency.Trim().All(char.IsAsciiLetter)) throw new ArgumentException("A three-letter currency is required.");
    }
    public static bool DepositSatisfied(decimal received,decimal required)=>WorkflowRules.DepositEligible(received,required);
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
            "alter table studio_frontdesk_requests add column if not exists deposit_message_id uuid",
            "drop index if exists ux_frontdesk_external_booking",
            "create unique index if not exists ux_frontdesk_external_booking_source on studio_frontdesk_requests(coalesce(payload->>'bookingSource','FrontDesk'),(payload->>'externalBookingId')) where nullif(payload->>'externalBookingId','') is not null"
        ];
        using var connection=database.OpenConnection();using var tx=connection.BeginTransaction();
        foreach(string statement in sql)Execute(connection,tx,statement);
        tx.Commit();
    }
    public IReadOnlyList<FrontDeskRequest> GetAll()
    {
        using var connection=database.OpenConnection();using var command=new NpgsqlCommand("select r.id,r.payload::text,r.status,r.job_id,greatest(0,coalesce((select sum(d.amount) from studio_frontdesk_deposits d where d.request_id=r.id),0)+coalesce((select sum(e.amount) from studio_financial_events e where e.job_id=r.job_id and e.kind in ('Refund','Reversal')),0)),r.created_at_utc,(select v.intake_reference from studio_conversations v where v.id=(r.payload->>'conversationId')::uuid) from studio_frontdesk_requests r order by r.created_at_utc desc",connection);
        using var reader=command.ExecuteReader();List<FrontDeskRequest> result=[];
        while(reader.Read())result.Add(Read(reader));return result;
    }
    public FrontDeskRequest Create(PendingBookingRequest request,string user)
    {
        FrontDeskRequestRules.Validate(request);
        request=request with{ClientName=request.ClientName.Trim(),ClientEmail=request.ClientEmail.Trim(),Title=request.Title.Trim(),Currency=request.Currency.Trim().ToUpperInvariant(),ExternalBookingId=string.IsNullOrWhiteSpace(request.ExternalBookingId)?null:request.ExternalBookingId.Trim(),SelectedServiceIds=request.SelectedServiceIds?.Distinct().Order().ToArray()};
        using var connection=database.OpenConnection();using var tx=connection.BeginTransaction();
        if(request.ExternalBookingId is not null)
        {
            Execute(connection,tx,"select pg_advisory_xact_lock(hashtextextended($1,0))","booking:"+request.BookingSource+":"+request.ExternalBookingId);
            using var existing=new NpgsqlCommand("select r.id,r.payload::text,r.status,r.job_id,greatest(0,coalesce((select sum(d.amount) from studio_frontdesk_deposits d where d.request_id=r.id),0)+coalesce((select sum(e.amount) from studio_financial_events e where e.job_id=r.job_id and e.kind in ('Refund','Reversal')),0)),r.created_at_utc from studio_frontdesk_requests r where payload->>'externalBookingId'=$1 and coalesce(payload->>'bookingSource','FrontDesk')=$2",connection,tx);
            existing.Parameters.AddWithValue(request.ExternalBookingId);existing.Parameters.AddWithValue(request.BookingSource);using var reader=existing.ExecuteReader();
            if(reader.Read())
            {
                FrontDeskRequest prior=Read(reader);
                // The server's deposit policy can change between retries; compare the submitted booking facts.
                if(JsonSerializer.Serialize(prior.Request with { RequiredDeposit=request.RequiredDeposit },JsonOptions) != JsonSerializer.Serialize(request,JsonOptions))throw new ArgumentException("This booking reference was already used with different details.");
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
        Guid job=CreatePendingJob(connection,tx,request,user);
        Execute(connection,tx,"insert into studio_frontdesk_requests(id,workspace_id,payload,created_by,job_id) values($1,$2,$3::jsonb,$4,$5)",id,request.WorkspaceId,JsonSerializer.Serialize(request,JsonOptions),user,job);
        tx.Commit();return GetAll().Single(x=>x.Id==id);
    }
    public bool HasPendingConversation(Guid conversationId)
    {
        using var connection=database.OpenConnection();
        using var command=new NpgsqlCommand("select exists(select 1 from studio_frontdesk_requests where status<>'Cancelled' and payload->>'conversationId'=$1)",connection);
        command.Parameters.AddWithValue(conversationId.ToString("D"));return (bool)command.ExecuteScalar()!;
    }
    public object MarkRequested(Guid id,string actor,string channel="Email",IConfiguration? configuration=null)
    {
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        if(channel is not ("Email" or "WhatsApp"))throw new ArgumentException("Choose Email or WhatsApp.");
        Guid conversation;Guid? prior;string? from;PendingBookingRequest request;string status;string? phone;
        using(var q=new NpgsqlCommand("select r.payload::text,r.status,j.conversation_id,w.email_address,r.deposit_message_id,j.status,coalesce(nullif(cli.phone,''),r.payload->>'clientPhone') from studio_frontdesk_requests r join studio_jobs j on j.id=r.job_id join studio_workspaces w on w.id=j.workspace_id left join studio_clients cli on cli.id=j.client_id where r.id=$1 for update of r,j",c,tx))
        {
            q.Parameters.AddWithValue(id);using var r=q.ExecuteReader();if(!r.Read())throw new KeyNotFoundException();
            request=JsonSerializer.Deserialize<PendingBookingRequest>(r.GetString(0),JsonOptions)!;
            status=r.GetString(1);conversation=r.GetGuid(2);from=r.IsDBNull(3)?null:r.GetString(3);prior=r.IsDBNull(4)?null:r.GetGuid(4);phone=r.IsDBNull(6)?null:r.GetString(6);
            if(r.GetString(5)!="Pending")throw new ArgumentException("Deposit requests belong to Pending Jobs; use Billing for confirmed Jobs.");
        }
        if(status is "Confirmed" or "Cancelled")throw new ArgumentException("This booking is no longer pending.");
        if(prior is Guid previous)return new{messageId=previous,state="AlreadyQueued",deliveryReceiptAvailable=false};
        if(channel=="Email"&&string.IsNullOrWhiteSpace(from))throw new ArgumentException("Configure an outbound workspace email identity before requesting a deposit.");
        if(channel=="Email"&&string.IsNullOrWhiteSpace(request.ClientEmail))throw new ArgumentException("A client email address is required for email deposit requests.");
        if(request.RequiredDeposit<=0)throw new ArgumentException("No deposit is required for this booking.");
        string recipient=channel=="Email"?request.ClientEmail:DepositDeliveryRules.Mobile(phone);
        Guid message=Guid.NewGuid();
        string body=$"Hello {request.ClientName},\n\nPlease arrange the required deposit of {request.Currency} {request.RequiredDeposit:0.00} for {request.Title}. Contact Bryan's to arrange payment and provide your receipt/reference.\n\nBooking reference: {id:D}\nThis is a deposit request, not confirmation of payment or booking.\n\nBryan's";
        var metadata=new Dictionary<string,string>();
        if(channel=="WhatsApp"&&!string.IsNullOrWhiteSpace(configuration?["Messaging:WhatsApp:DepositTemplateName"]))
        {
            metadata["WhatsAppTemplateName"]=configuration["Messaging:WhatsApp:DepositTemplateName"]!;
            metadata["WhatsAppTemplateLanguage"]=configuration["Messaging:WhatsApp:DepositTemplateLanguage"]??"en_US";
            metadata["WhatsAppTemplateParameters"]=(configuration["Messaging:WhatsApp:DepositTemplateParameters"]??"{client}|{job}|{amount}|{reference}")
                .Replace("{client}",request.ClientName.Replace('|',' ')).Replace("{job}",request.Title.Replace('|',' ')).Replace("{amount}",$"{request.Currency} {request.RequiredDeposit:0.00}").Replace("{reference}",id.ToString("D"));
        }
        Execute(c,tx,"insert into studio_outbound_email(id,conversation_id,from_address,to_address,subject,body,status,created_at_utc,kind,sent_by_user_name,sending_identity_type,delivery_channel,delivery_metadata) values($1,$2,$3,$4,$5,$6,'Pending',now(),'DepositRequest',$7,$8,$9,$10::jsonb)",message,conversation,channel=="Email"?from!:"Configured WhatsApp provider",recipient,"Deposit request — "+request.Title,body,actor,channel=="Email"?"Workspace":"Provider",channel,JsonSerializer.Serialize(metadata));
        Execute(c,tx,"update studio_frontdesk_requests set status='AwaitingDeposit',deposit_requested_at_utc=now(),deposit_message_id=$2 where id=$1",id,message);
        tx.Commit();return new{messageId=message,state="Queued",channel,deliveryReceiptAvailable=false};
    }
    public object DepositDelivery(Guid id)
    {
        using var c=database.OpenConnection();using var q=new NpgsqlCommand("select r.deposit_message_id,e.status,e.attempts,e.last_error,e.provider_accepted_at_utc,e.provider_message_id,e.delivery_channel from studio_frontdesk_requests r left join studio_outbound_email e on e.id=r.deposit_message_id where r.id=$1",c);
        q.Parameters.AddWithValue(id);using var r=q.ExecuteReader();if(!r.Read())throw new KeyNotFoundException();
        return new{messageId=r.IsDBNull(0)?(Guid?)null:r.GetGuid(0),state=r.IsDBNull(1)?"NotRequested":r.GetString(1),attempts=r.IsDBNull(2)?0:r.GetInt32(2),lastError=r.IsDBNull(3)?null:r.GetString(3),providerAcceptedAtUtc=r.IsDBNull(4)?(DateTimeOffset?)null:r.GetFieldValue<DateTimeOffset>(4),providerMessageId=r.IsDBNull(5)?null:r.GetString(5),channel=r.IsDBNull(6)?null:r.GetString(6),deliveryReceiptAvailable=false};
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
        using(var get=new NpgsqlCommand("select r.id,r.payload::text,r.status,r.job_id,greatest(0,coalesce((select sum(d.amount) from studio_frontdesk_deposits d where d.request_id=r.id),0)+coalesce((select sum(e.amount) from studio_financial_events e where e.job_id=r.job_id and e.kind in ('Refund','Reversal')),0)),r.created_at_utc from studio_frontdesk_requests r where r.id=$1 for update",connection,tx))
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
        if(current.Status is "Confirmed" or "Cancelled")throw new ArgumentException("Use Job Billing for payments after confirmation, or resolve the cancelled booking first.");
        Guid depositId=Guid.NewGuid();
        Execute(connection,tx,"insert into studio_frontdesk_deposits(id,request_id,amount,currency,provider,reference,recorded_by) values($1,$2,$3,$4,$5,$6,$7)",depositId,id,payment.Amount,currency,provider,reference,user);
        if(current.JobId is Guid job)
            Execute(connection,tx,"insert into studio_financial_events(id,job_id,kind,amount,note,recorded_by,created_at_utc) values($1,$2,'Deposit',$3,$4,$5,now()) on conflict(id) do nothing",depositId,job,payment.Amount,currency+" / "+provider+" / "+reference,user);
        Execute(connection,tx,"update studio_frontdesk_requests set status='AwaitingConfirmation' where id=$1",id);
        tx.Commit();return GetAll().Single(x=>x.Id==id);
    }
    internal static Guid CreatePendingJob(NpgsqlConnection c,NpgsqlTransaction tx,PendingBookingRequest r,string user,bool createClient=true)
    {
        Guid conversation=r.ConversationId??Guid.NewGuid();
        if(r.ConversationId.HasValue)
        {
            using var q=new NpgsqlCommand("select job_id from studio_conversations where id=$1 for update",c,tx);q.Parameters.AddWithValue(conversation);
            if(q.ExecuteScalar() is Guid existing)return existing;
        }
        else Execute(c,tx,"insert into studio_conversations(id,workspace_id,subject,contact_name,contact_address,channel,is_closed,created_at_utc) values($1,$2,$3,$4,$5,'Email',false,now())",conversation,r.WorkspaceId,r.Title,r.ClientName,r.ClientEmail);
        Guid client=r.ClientId??Guid.Empty;
        if(client==Guid.Empty && r.ConversationId.HasValue && !createClient)
        {
            using var selected=new NpgsqlCommand("select client_id from studio_conversations where id=$1",c,tx);selected.Parameters.AddWithValue(conversation);
            if(selected.ExecuteScalar() is Guid linkedClient)client=linkedClient;
        }
        if(client==Guid.Empty && createClient)
        {
            Execute(c,tx,"select pg_advisory_xact_lock(hashtextextended($1,0))","client:"+r.ClientEmail.ToLowerInvariant()+":"+r.ClientName.ToLowerInvariant());
            using var q=new NpgsqlCommand("select id from studio_clients where lower(email)=lower($1) and lower(display_name)=lower($2) order by updated_at_utc desc limit 1",c,tx);q.Parameters.AddWithValue(r.ClientEmail);q.Parameters.AddWithValue(r.ClientName);
            if(q.ExecuteScalar() is Guid existing)client=existing;
            else {client=Guid.NewGuid();Execute(c,tx,"insert into studio_clients(id,display_name,email,phone) values($1,$2,$3,$4)",client,r.ClientName,r.ClientEmail,r.ClientPhone??"");}
        }
        Execute(c,tx,"select set_config('studio.actor',$1,true)",user);
        Guid job=Guid.NewGuid();
        Execute(c,tx,"insert into studio_jobs(id,workspace_id,conversation_id,title,client_name,status,event_date,created_at_utc,client_id) values($1,$2,$3,$4,$5,'Pending',$6,now(),$7)",job,r.WorkspaceId,conversation,r.Title,r.ClientName,r.EventDate,client==Guid.Empty?DBNull.Value:(object)client);
        Execute(c,tx,"update studio_conversations set job_id=$2 where id=$1",conversation,job);
        Execute(c,tx,"insert into studio_messages(id,conversation_id,author_display_name,visibility,body,created_at_utc) values($1,$2,$3,'Internal','Booking received; Pending Job created for FrontDesk review.',now())",Guid.NewGuid(),conversation,user);
        if(r.JobTemplateId is Guid templateId)
        {
            var template=CommercialTemplateStore.Get(c,tx,templateId)??throw new ArgumentException("Job Template was not found.");
            CommercialTemplateStore.ApplyNewJob(c,tx,job,template,Guid.NewGuid(),user);
            if(r.SelectedServiceIds is not null)
            {
                CommercialTemplateStore.Execute(c,tx,"update studio_job_catalog_agreements set workflow_selected=false where job_id=$1",job);
                foreach(var service in r.SelectedServiceIds.Distinct())
                {
                    using var check=CommercialTemplateStore.Command(c,tx,"select exists(select 1 from studio_pos_catalog where id=$1 and is_active)",service);
                    if(!(bool)check.ExecuteScalar()!)throw new ArgumentException("Choose an active catalog service.");
                    CommercialTemplateStore.Execute(c,tx,"insert into studio_job_catalog_agreements(id,job_id,catalog_item_id,quantity) select $1,$2,$3,1 where not exists(select 1 from studio_job_catalog_agreements where job_id=$2 and catalog_item_id=$3)",Guid.NewGuid(),job,service);
                    CommercialTemplateStore.Execute(c,tx,"update studio_job_catalog_agreements set workflow_selected=true where job_id=$1 and catalog_item_id=$2",job,service);
                }
            }
        }
        return job;
    }
    private static FrontDeskRequest Read(NpgsqlDataReader r)=>new(r.GetGuid(0),JsonSerializer.Deserialize<PendingBookingRequest>(r.GetString(1),JsonOptions)!,r.GetString(2),r.IsDBNull(3)?null:r.GetGuid(3),r.GetDecimal(4),r.GetFieldValue<DateTimeOffset>(5),r.FieldCount>6&&!r.IsDBNull(6)?r.GetString(6):null);
    private static void Execute(NpgsqlConnection c,NpgsqlTransaction tx,string sql,params object[] args)
    {using var command=new NpgsqlCommand(sql,c,tx);foreach(object value in args)command.Parameters.AddWithValue(value);command.ExecuteNonQuery();}
}

public static class FrontDeskBookingRequestEndpoints
{
    public static void MapFrontDeskBookingRequests(this WebApplication app)
    {
        var group=app.MapGroup("/api/frontdesk").RequireAuthorization();
        group.MapGet("/booking-requests",(ClaimsPrincipal user,StudioDatabase db)=>Allowed(user,db)?Results.Ok(new FrontDeskBookingRequestStore(db).GetAll()):Results.Forbid());
        group.MapPost("/gallery-access-requests/{conversationId:guid}/publish",async(Guid conversationId,ClaimsPrincipal user,StudioDatabase db,IConfiguration configuration,CancellationToken ct)=>
        {
            if(!Allowed(user,db))return Results.Forbid();
            Guid? galleryId=null;bool publicationUpdate=false;
            using(var c=db.OpenConnection())
            {
                using(var q=new NpgsqlCommand("select publication_id from studio_gallery_exports where conversation_id=$1 and state='PendingReview'",c))
                {q.Parameters.AddWithValue(conversationId);if(q.ExecuteScalar() is Guid publication){galleryId=publication;publicationUpdate=true;}}
                if(galleryId is null)
                {
                    using var q=new NpgsqlCommand("select gallery_id from studio_website_gallery_access_requests where conversation_id=$1",c);
                    q.Parameters.AddWithValue(conversationId);var value=q.ExecuteScalar();
                    if(value is null)return Results.NotFound();
                    galleryId=value is DBNull?null:(Guid?)value;
                }
            }
            if(galleryId is null)return Results.BadRequest(new{error="This request has no linked gallery. Link a gallery in Studio before publishing."});
            if(!GalleryPushWorker.CanPublish(db,galleryId.Value))return Results.Conflict(new{error="This gallery is no longer approved for this client. Review the current publication in Studio."});
            try
            {
                using var site=new PublicSiteClient(configuration);
                await site.Post($"/api/v1/galleries/{galleryId.Value:D}/publish",new{},ct);
                using var c=db.OpenConnection();
                if(publicationUpdate)
                {
                    using(var q=new NpgsqlCommand("update studio_gallery_exports set state='Published',last_error=null,updated_at_utc=now() where publication_id=$1 and state='PendingReview'",c))
                    {q.Parameters.AddWithValue(galleryId.Value);q.ExecuteNonQuery();}
                    using(var q=new NpgsqlCommand("update studio_conversations set is_closed=true where id=$1",c))
                    {q.Parameters.AddWithValue(conversationId);q.ExecuteNonQuery();}
                    using(var q=new NpgsqlCommand("update studio_website_gallery_access_requests set reviewed_by=$2,reviewed_at_utc=now() where gallery_id=$1 and reviewed_at_utc is null",c))
                    {q.Parameters.AddWithValue(galleryId.Value);q.Parameters.AddWithValue(user.FindFirstValue("login_name")??user.Identity?.Name??"Front Desk");q.ExecuteNonQuery();}
                    using(var q=new NpgsqlCommand("update studio_conversations set is_closed=true where id in (select conversation_id from studio_website_gallery_access_requests where gallery_id=$1)",c))
                    {q.Parameters.AddWithValue(galleryId.Value);q.ExecuteNonQuery();}
                }
                else
                {
                    using(var q=new NpgsqlCommand("update studio_website_gallery_access_requests set reviewed_by=$2,reviewed_at_utc=now() where conversation_id=$1",c))
                    {q.Parameters.AddWithValue(conversationId);q.Parameters.AddWithValue(user.FindFirstValue("login_name")??user.Identity?.Name??"Front Desk");q.ExecuteNonQuery();}
                    using(var q=new NpgsqlCommand("update studio_gallery_exports set state='Published',last_error=null,updated_at_utc=now() where publication_id=$1 and state='PendingReview'",c))
                    {q.Parameters.AddWithValue(galleryId.Value);q.ExecuteNonQuery();}
                    using(var q=new NpgsqlCommand("update studio_conversations set is_closed=true where id in ($1,(select conversation_id from studio_gallery_exports where publication_id=$2))",c))
                    {q.Parameters.AddWithValue(conversationId);q.Parameters.AddWithValue(galleryId.Value);q.ExecuteNonQuery();}
                }
                return Results.Ok(new{published=true,galleryId});
            }
            catch(HttpRequestException)
            {
                return Results.Json(new{error="The Public Site could not publish the gallery. Check that all gallery media is staged, then retry."},statusCode:502);
            }
        });
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
        group.MapPost("/booking-requests",(PendingBookingRequest request,ClaimsPrincipal user,StudioDatabase db,ILogger<FrontDeskBookingRequestStore> logger,HttpContext context)=>
        {
            var started=System.Diagnostics.Stopwatch.StartNew();
            var traceId=context.TraceIdentifier;
            var actor=Name(user);
            logger.LogInformation("Front Desk pending booking save started. TraceId={TraceId} User={User} WorkspaceId={WorkspaceId} ConversationId={ConversationId} EventDate={EventDate} HasEventDate={HasEventDate}",traceId,actor,request.WorkspaceId,request.ConversationId,request.EventDate,request.EventDate!=default);
            if(!Allowed(user,db))
            {
                logger.LogWarning("Front Desk pending booking save denied. TraceId={TraceId} User={User}",traceId,actor);
                return Results.Forbid();
            }
            try
            {
                FrontDeskRequest saved=new FrontDeskBookingRequestStore(db).Create(request,actor);
                started.Stop();
                logger.LogInformation("Front Desk pending booking save succeeded. TraceId={TraceId} RequestId={RequestId} JobId={JobId} WorkspaceId={WorkspaceId} ConversationId={ConversationId} EventDate={EventDate} ElapsedMs={ElapsedMs}",traceId,saved.Id,saved.JobId,request.WorkspaceId,request.ConversationId,saved.Request.EventDate,started.ElapsedMilliseconds);
                return Results.Ok(saved);
            }
            catch(ArgumentException ex)
            {
                started.Stop();
                logger.LogWarning(ex,"Front Desk pending booking save rejected. TraceId={TraceId} WorkspaceId={WorkspaceId} ConversationId={ConversationId} EventDate={EventDate} ElapsedMs={ElapsedMs}",traceId,request.WorkspaceId,request.ConversationId,request.EventDate,started.ElapsedMilliseconds);
                return Results.BadRequest(new{error=ex.Message});
            }
            catch(KeyNotFoundException ex)
            {
                started.Stop();
                logger.LogWarning(ex,"Front Desk pending booking save target not found. TraceId={TraceId} WorkspaceId={WorkspaceId} ConversationId={ConversationId} ElapsedMs={ElapsedMs}",traceId,request.WorkspaceId,request.ConversationId,started.ElapsedMilliseconds);
                return Results.NotFound();
            }
            catch(Exception ex)
            {
                started.Stop();
                logger.LogError(ex,"Front Desk pending booking save failed. TraceId={TraceId} WorkspaceId={WorkspaceId} ConversationId={ConversationId} EventDate={EventDate} ElapsedMs={ElapsedMs}",traceId,request.WorkspaceId,request.ConversationId,request.EventDate,started.ElapsedMilliseconds);
                throw;
            }
        });
        group.MapGet("/deposit-delivery-channels",(ClaimsPrincipal user,StudioDatabase db,IEnumerable<Common.Messaging.IExternalMessageChannel> channels)=>
            Allowed(user,db)?Results.Ok(channels.Where(x=>x.Channel is Common.Messaging.MessageChannel.MsEmail or Common.Messaging.MessageChannel.WhatsApp).Select(x=>x.Channel==Common.Messaging.MessageChannel.MsEmail?"Email":"WhatsApp").Distinct().ToArray()):Results.Forbid());
        group.MapPost("/booking-requests/{id:guid}/deposit-requested",(Guid id,string? channel,ClaimsPrincipal user,StudioDatabase db,IConfiguration configuration,IEnumerable<Common.Messaging.IExternalMessageChannel> channels)=>
        {
            if(!Allowed(user,db))return Results.Forbid();
            string chosen=channel??"Email";
            if(chosen is not ("Email" or "WhatsApp"))return Results.BadRequest(new{error="Choose Email or WhatsApp."});
            var kind=chosen=="Email"?Common.Messaging.MessageChannel.MsEmail:Common.Messaging.MessageChannel.WhatsApp;
            if(!channels.Any(x=>x.Channel==kind))return Results.Json(new{error=$"Configure the {chosen} delivery provider before sending deposit requests."},statusCode:503);
            return Run(()=>Results.Ok(new FrontDeskBookingRequestStore(db).MarkRequested(id,Name(user),chosen,configuration)));
        });
        group.MapGet("/booking-requests/{id:guid}/deposit-delivery",(Guid id,ClaimsPrincipal user,StudioDatabase db)=>Allowed(user,db)?Run(()=>Results.Ok(new FrontDeskBookingRequestStore(db).DepositDelivery(id))):Results.Forbid());
        group.MapPost("/booking-requests/{id:guid}/deposits",(Guid id,VerifiedDepositRequest request,ClaimsPrincipal user,StudioDatabase db)=>Allowed(user,db)?Run(()=>Results.Ok(new FrontDeskBookingRequestStore(db).RecordDeposit(id,request,Name(user)))):Results.Forbid());
    }
    private static bool Allowed(ClaimsPrincipal user,StudioDatabase db)=>user.IsInRole("Administrator") || (new GlobalOperationalRoleStore(db).HasPermission(user,"ViewAllJobs") && new GlobalOperationalRoleStore(db).HasPermission(user,"Billing"));
    private static string Name(ClaimsPrincipal user)=>user.FindFirstValue("login_name")??user.Identity?.Name??throw new UnauthorizedAccessException();
    private static IResult Run(Func<IResult> action)
    {try{return action();}catch(ArgumentException e){return Results.BadRequest(new{error=e.Message});}catch(KeyNotFoundException){return Results.NotFound();}}
}




