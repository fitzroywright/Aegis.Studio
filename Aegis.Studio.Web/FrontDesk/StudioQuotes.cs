using System.Security.Claims;
using System.Text.Json;
using Aegis.Studio.Web.Media;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.FrontDesk;

public sealed record ClientQuoteRequest(Guid ClientId,Guid WorkspaceId,string Title,DateOnly EventDate,string? Instructions,InvoiceRequest Estimate,decimal RequiredDeposit);
public sealed record QuoteAcceptance(string Reference);
public sealed class StudioQuoteStore(StudioDatabase db)
{
    private static readonly JsonSerializerOptions JsonOptions=new(JsonSerializerDefaults.Web);
    public void Migrate(){using var c=db.OpenConnection();using var tx=c.BeginTransaction();MediaProductionStore.Execute(c,tx,"create table if not exists studio_client_quotes(id uuid primary key,client_id uuid not null references studio_clients(id),payload jsonb not null,total numeric(18,2) not null,status text not null default 'Draft',notification_id uuid null,acceptance_reference text null,request_id uuid null references studio_frontdesk_requests(id),created_by text not null,created_at_utc timestamptz not null default now())");tx.Commit();}
    public JsonElement List(Guid client){using var c=db.OpenConnection();using var cmd=new NpgsqlCommand("select coalesce(jsonb_agg(jsonb_build_object('id',q.id,'request',q.payload,'total',q.total,'status',q.status,'deliveryStatus',n.status,'deliveryError',n.last_error,'acceptanceReference',q.acceptance_reference,'requestId',q.request_id) order by q.created_at_utc desc),'[]'::jsonb)::text from studio_client_quotes q left join studio_notifications n on n.id=q.notification_id where q.client_id=$1",c);cmd.Parameters.AddWithValue(client);return JsonSerializer.Deserialize<JsonElement>((string)cmd.ExecuteScalar()!);}
    public Guid Create(ClientQuoteRequest request,string user)
    {
        if(string.IsNullOrWhiteSpace(request.Title)||request.Title.Length>200||request.EventDate==default||(request.Instructions?.Length??0)>4000)throw new ArgumentException("Supply a title, date and instructions of at most 4000 characters.");
        var estimate=request.Estimate;if(estimate.Lines is null||estimate.Lines.Count==0||estimate.Lines.Count>100||estimate.Lines.Any(l=>string.IsNullOrWhiteSpace(l.Description)||l.Description.Length>500||l.Quantity<=0||l.Quantity>100000||l.UnitPrice<0||l.UnitPrice>999999999)||estimate.TaxRatePercent<0||estimate.TaxRatePercent>100)throw new ArgumentException("Supply valid quote lines and tax rate.");
        decimal subtotal=estimate.Lines.Sum(l=>l.Quantity*l.UnitPrice),total=subtotal+Math.Round(subtotal*estimate.TaxRatePercent/100,2);
        if(total<=0||total>999999999||request.RequiredDeposit<=0||request.RequiredDeposit>total||request.RequiredDeposit!=decimal.Round(request.RequiredDeposit,2)||estimate.Currency!="JMD")throw new ArgumentException("Quote and deposit must be valid JMD amounts; deposit must not exceed total.");
        using var c=db.OpenConnection();using var tx=c.BeginTransaction();
        if(MediaProductionStore.Scalar(c,tx,"select id from studio_clients where id=$1",request.ClientId) is null||MediaProductionStore.Scalar(c,tx,"select id from studio_workspaces where id=$1 and is_enabled",request.WorkspaceId) is null)throw new ArgumentException("Select a client and enabled workspace.");
        Guid id=Guid.NewGuid();MediaProductionStore.Execute(c,tx,"insert into studio_client_quotes(id,client_id,payload,total,created_by) values($1,$2,$3::jsonb,$4,$5)",id,request.ClientId,JsonSerializer.Serialize(request,JsonOptions),total,user);tx.Commit();return id;
    }
    public void Send(Guid id,string user)
    {
        using var c=db.OpenConnection();using var tx=c.BeginTransaction();
        using var cmd=new NpgsqlCommand("select q.payload::text,q.total,q.status,c.email,c.display_name from studio_client_quotes q join studio_clients c on c.id=q.client_id where q.id=$1 for update of q",c,tx);cmd.Parameters.AddWithValue(id);
        ClientQuoteRequest request;decimal total;string email,name;
        using(var r=cmd.ExecuteReader()){if(!r.Read())throw new KeyNotFoundException();if(r.GetString(2)!="Draft")throw new ArgumentException("This quote has already been queued or accepted.");request=JsonSerializer.Deserialize<ClientQuoteRequest>(r.GetString(0),JsonOptions)!;total=r.GetDecimal(1);email=r.IsDBNull(3)?"":r.GetString(3);name=r.GetString(4);}
        if(!System.Net.Mail.MailAddress.TryCreate(email,out _))throw new ArgumentException("Client needs a valid email address before sending a quote.");
        Guid notification=Guid.NewGuid();string body=$"Hello {name},\n\nQuote: {request.Title}\nEvent: {request.EventDate:yyyy-MM-dd}\n"+string.Join("\n",request.Estimate.Lines.Select(l=>$"{l.Description}: {l.Quantity} × JMD {l.UnitPrice:0.00}"))+$"\nTotal including tax: JMD {total:0.00}\nDeposit required: JMD {request.RequiredDeposit:0.00}\n{request.Instructions}\n\nPlease reply to confirm acceptance. Quote reference: {id}";
        MediaProductionStore.Execute(c,tx,"insert into studio_notifications(id,user_name,channel,event_type,subject,body,reference_type,reference_id,status,destination,attempts,is_read,created_at_utc) values($1,$2,'Email','ClientQuote',$3,$4,'ClientQuote',$5,'Pending',$6,0,false,now())",notification,"client:"+request.ClientId,"Quote: "+request.Title,body,id.ToString("D"),email);
        MediaProductionStore.Execute(c,tx,"update studio_client_quotes set status='Queued',notification_id=$2 where id=$1",id,notification);tx.Commit();
    }
    public void Accept(Guid id,string reference)
    {
        if(string.IsNullOrWhiteSpace(reference)||reference.Length>500)throw new ArgumentException("Record the client's acceptance reference (email, call or signed document).");
        using var c=db.OpenConnection();using var tx=c.BeginTransaction();if(MediaProductionStore.Execute(c,tx,"update studio_client_quotes set status='Accepted',acceptance_reference=$2 where id=$1 and status in ('Draft','Queued')",id,reference.Trim())!=1)throw new ArgumentException("Quote is missing, already accepted or converted.");tx.Commit();
    }
    public Guid ConvertToBooking(Guid id,string user)
    {
        using var c=db.OpenConnection();using var tx=c.BeginTransaction();using var cmd=new NpgsqlCommand("select q.payload::text,q.status,q.request_id,c.display_name,c.email from studio_client_quotes q join studio_clients c on c.id=q.client_id where q.id=$1 for update of q",c,tx);cmd.Parameters.AddWithValue(id);
        ClientQuoteRequest quote;string name,email;using(var r=cmd.ExecuteReader()){if(!r.Read())throw new KeyNotFoundException();if(!r.IsDBNull(2))return r.GetGuid(2);if(r.GetString(1)!="Accepted")throw new ArgumentException("Record acceptance before converting the quote.");quote=JsonSerializer.Deserialize<ClientQuoteRequest>(r.GetString(0),JsonOptions)!;name=r.GetString(3);email=r.IsDBNull(4)?"":r.GetString(4);}
        if(MediaProductionStore.Scalar(c,tx,"select id from studio_workspaces where id=$1 and is_enabled for share",quote.WorkspaceId) is null)throw new ArgumentException("Workspace is archived.");
        var request=new PendingBookingRequest(quote.WorkspaceId,name,email,quote.Title,quote.EventDate,quote.RequiredDeposit,"JMD",quote.Instructions,ClientId:quote.ClientId);FrontDeskRequestRules.Validate(request);
        Guid job=FrontDeskBookingRequestStore.CreatePendingJob(c,tx,request,user);
        Guid booking=Guid.NewGuid();MediaProductionStore.Execute(c,tx,"insert into studio_frontdesk_requests(id,workspace_id,payload,created_by,job_id) values($1,$2,$3::jsonb,$4,$5)",booking,quote.WorkspaceId,JsonSerializer.Serialize(request,JsonOptions),user,job);
        MediaProductionStore.Execute(c,tx,"update studio_client_quotes set status='Converted',request_id=$2 where id=$1",id,booking);tx.Commit();return booking;
    }
}
public static class StudioQuoteEndpoints
{
    public static void MapStudioQuotes(this WebApplication app)
    {
        new StudioQuoteStore(app.Services.GetRequiredService<StudioDatabase>()).Migrate();var group=app.MapGroup("/api/frontdesk/quotes").RequireAuthorization();
        group.MapGet("",(Guid clientId,ClaimsPrincipal user,StudioDatabase db)=>Run(user,db,()=>Results.Ok(new StudioQuoteStore(db).List(clientId))));
        group.MapPost("",(ClientQuoteRequest request,ClaimsPrincipal user,StudioDatabase db)=>Run(user,db,()=>Results.Ok(new{id=new StudioQuoteStore(db).Create(request,Name(user))})));
        group.MapPost("/{id:guid}/send",(Guid id,ClaimsPrincipal user,StudioDatabase db)=>Run(user,db,()=>{new StudioQuoteStore(db).Send(id,Name(user));return Results.NoContent();}));
        group.MapPost("/{id:guid}/accept",(Guid id,QuoteAcceptance request,ClaimsPrincipal user,StudioDatabase db)=>Run(user,db,()=>{new StudioQuoteStore(db).Accept(id,request.Reference);return Results.NoContent();}));
        group.MapPost("/{id:guid}/convert",(Guid id,ClaimsPrincipal user,StudioDatabase db)=>Run(user,db,()=>Results.Ok(new{requestId=new StudioQuoteStore(db).ConvertToBooking(id,Name(user))})));
    }
    private static string Name(ClaimsPrincipal user)=>user.FindFirstValue("login_name")??user.Identity!.Name!;
    private static IResult Run(ClaimsPrincipal user,StudioDatabase db,Func<IResult> action){if(!user.IsInRole("Administrator")&&!(new GlobalOperationalRoleStore(db).HasPermission(user,"ViewAllJobs")&&new GlobalOperationalRoleStore(db).HasPermission(user,"Billing")))return Results.Forbid();try{return action();}catch(ArgumentException e){return Results.BadRequest(new{error=e.Message});}catch(KeyNotFoundException){return Results.NotFound();}}
}

