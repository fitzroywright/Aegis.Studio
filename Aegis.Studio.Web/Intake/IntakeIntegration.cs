using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aegis.Studio.Web.FrontDesk;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Workflow;
using Npgsql;
namespace Aegis.Studio.Web.Intake;

public static class IntakeSchema
{
    public static void Ensure(StudioDatabase database)
    {
        using var c=database.OpenConnection();using var input=typeof(IntakeSchema).Assembly.GetManifestResourceStream("Aegis.Studio.Web.Intake.IntakeSchema.sql")!;
        using var r=new StreamReader(input);WorkflowStore.Sql(c,null,r.ReadToEnd());
    }
}
public sealed class PublicSiteClient : IDisposable
{
    readonly HttpClient http;readonly Uri root;
    public PublicSiteClient(IConfiguration config)
    {
        root=new Uri(config["PublicSite:BaseUrl"]??throw new InvalidOperationException("Configure PublicSite:BaseUrl."));
        if(root.Scheme!="https"||!string.IsNullOrEmpty(root.UserInfo)||root.AbsolutePath!="/")throw new InvalidOperationException("PublicSite:BaseUrl must be an HTTPS origin.");
        http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){BaseAddress=root,Timeout=TimeSpan.FromSeconds(45)};
        string secret=config["PublicSite:IntegrationToken"]??"";if(secret.Length<32)throw new InvalidOperationException("Configure a scoped public site integration token.");
        http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",secret);
    }
    public async Task<JsonElement> Get(string path,CancellationToken ct)
    {using var r=await http.GetAsync(path,ct);r.EnsureSuccessStatusCode();return await r.Content.ReadFromJsonAsync<JsonElement>(cancellationToken:ct);}
    public async Task Post(string path,object data,CancellationToken ct){_ = await PostJson(path,data,ct);}
    public async Task<JsonElement> PostJson(string path,object data,CancellationToken ct)
    {using var r=await http.PostAsJsonAsync(path,data,ct);r.EnsureSuccessStatusCode();return await r.Content.ReadFromJsonAsync<JsonElement>(cancellationToken:ct);}
    public async Task PutFile(string path,Stream content,string type,CancellationToken ct)
    {using var body=new StreamContent(content);body.Headers.ContentType=new(type);using var r=await http.PutAsync(path,body,ct);r.EnsureSuccessStatusCode();}
    public async Task<byte[]> File(Guid id,CancellationToken ct)
    {using var r=await http.GetAsync($"/api/public-site/v1/attachments/{id:D}",HttpCompletionOption.ResponseHeadersRead,ct);r.EnsureSuccessStatusCode();if(r.Content.Headers.ContentLength>2_500_000)throw new InvalidOperationException("Attachment exceeds limit.");await using var s=await r.Content.ReadAsStreamAsync(ct);using var m=new MemoryStream();var buffer=new byte[8192];int n;while((n=await s.ReadAsync(buffer,ct))>0){if(m.Length+n>2_500_000)throw new InvalidOperationException("Attachment exceeds limit.");m.Write(buffer,0,n);}return m.ToArray();}
    public void Dispose()=>http.Dispose();
}
public sealed class WebsiteIntakeStore(StudioDatabase database,WorkspaceStore workspaces,IConfiguration configuration)
{
    public static string Text(JsonElement p,string key,string fallback="")=>p.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()??fallback:fallback;
    public Guid Import(string source,JsonElement p)
    {
        Guid external=p.GetProperty("id").GetGuid();long revision=p.GetProperty("revision").GetInt64();string reference=Text(p,"intakeReference");
        if(revision<1||!Regex.IsMatch(reference,@"^\d{4}-W-\d{6}$"))throw new ArgumentException("Invalid website revision or intake reference.");
        var destination=Text(p,"destination");var w=workspaces.FindByRouteKey(destination)??workspaces.FindByAddress(destination);
        if(w is null||!w.IsEnabled)w=workspaces.Get(WorkflowRules.UnassignedWorkspaceId)??throw new InvalidOperationException("Unassigned workspace unavailable.");
        var rows=p.GetProperty("roster");IntakeRosterStore.Validate(rows);
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        WorkflowStore.Sql(c,tx,"select pg_advisory_xact_lock(hashtextextended($1,0))","website-intake:"+source+":"+external);
        Guid? conversation=null;long previous=0;
        using(var q=WorkflowStore.Command(c,tx,"select conversation_id,revision from studio_website_imports where source=$1 and external_id=$2",source,external))using(var r=q.ExecuteReader())if(r.Read()){conversation=r.GetGuid(0);previous=r.GetInt64(1);}
        if(previous>=revision)return conversation!.Value;
        string name=Text(p,"clientName"),email=Text(p,"clientEmail"),title=Text(p,"sessionType","Website inquiry"),kind=Text(p,"kind");
        if(string.IsNullOrWhiteSpace(name)||name.Length>200||email.Length>320||title.Length>200||kind is not ("Booking" or "Inquiry"))throw new ArgumentException("Invalid intake details.");
        Guid cv=conversation??Guid.NewGuid();Guid? requestId=null;
        if(!conversation.HasValue)
        {
            // Exactly one existing client can be linked; importing an inquiry never creates a client.
            WorkflowStore.Sql(c,tx,"""
              insert into studio_conversations(id,workspace_id,subject,contact_name,contact_address,channel,is_closed,created_at_utc,intake_reference,client_id)
              values($1,$2,$3,$4,$5,'Website',false,$6,$7,(select case when count(*)=1 then min(id::text)::uuid end from studio_clients where studio_normalize_email(email)=studio_normalize_email($5) and $5<>''))
              """,cv,w.Id,string.IsNullOrWhiteSpace(title)?"Website inquiry":title,name,email,p.GetProperty("createdAtUtc").GetDateTimeOffset(),reference);
            WorkflowStore.Sql(c,tx,"insert into studio_messages(id,conversation_id,author_display_name,author_user_name,visibility,body,created_at_utc,external_message_id) values($1,$2,$3,$4,'External',$5,now(),$6)",Guid.NewGuid(),cv,name,email,Text(p,"notes","Website submission"),"website:"+source+":"+external);
            if(kind=="Booking")
            {
                var start=p.GetProperty("startsAt").GetDateTimeOffset();var end=p.GetProperty("endsAt").GetDateTimeOffset();
                var pending=new PendingBookingRequest(w.Id,name,email,title,DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(start,TimeZoneInfo.FindSystemTimeZoneById(Text(p,"timeZone","America/Jamaica"))).DateTime),Math.Max(0,configuration.GetValue<decimal?>("BookingApi:RequiredDeposit")??0),"JMD",Text(p,"notes"),ConversationId:cv,ExternalBookingId:source+":"+external,StartsAtUtc:start,EndsAtUtc:end,Location:Text(p,"location"),ClientPhone:Text(p,"clientPhone"),Destination:destination,BookingSource:"Website");
                FrontDeskRequestRules.Validate(pending);
                Guid job=FrontDeskBookingRequestStore.CreatePendingJob(c,tx,pending,"Website import",false);
                requestId=Guid.NewGuid();WorkflowStore.Sql(c,tx,"insert into studio_frontdesk_requests(id,workspace_id,payload,created_by,job_id) values($1,$2,$3::jsonb,'Website import',$4)",requestId,w.Id,JsonSerializer.Serialize(pending,PublicApi.PublicV1Store.Json),job);
            }
        }
        string? priorRows=WorkflowStore.Scalar(c,tx,"select (payload->'roster')::text from studio_website_imports where source=$1 and external_id=$2",source,external) as string;
        bool rosterChanged=priorRows is null||!JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(priorRows),rows);
        if(rosterChanged)
        {
            string? editedBy=WorkflowStore.Scalar(c,tx,"select updated_by from studio_intake_rosters where conversation_id=$1",cv) as string;
            if(editedBy is not null && editedBy!="Website")
                WorkflowStore.Sql(c,tx,"insert into studio_intake_roster_conflicts values($1,$2::jsonb,$3,now()) on conflict(conversation_id) do update set rows=$2::jsonb,revision=$3,created_at_utc=now()",cv,rows.GetRawText(),revision);
            else
            {
                WorkflowStore.Sql(c,tx,"insert into studio_intake_rosters(conversation_id,revision,rows,updated_by) values($1,1,$2::jsonb,'Website') on conflict(conversation_id) do update set revision=studio_intake_rosters.revision+1,rows=$2::jsonb,updated_by='Website',updated_at_utc=now()",cv,rows.GetRawText());
                WorkflowStore.Sql(c,tx,"select studio_apply_intake_roster($1)",cv);
            }
        }
        WorkflowStore.Sql(c,tx,"insert into studio_website_imports(source,external_id,revision,conversation_id,request_id,payload) values($1,$2,$3,$4,$5,$6::jsonb) on conflict(source,external_id) do update set revision=$3,payload=$6::jsonb",source,external,revision,cv,requestId,p.GetRawText());
        if(p.TryGetProperty("attachments",out var files))foreach(var f in files.EnumerateArray())
            WorkflowStore.Sql(c,tx,"insert into studio_intake_attachments(id,conversation_id,metadata,state) values($1,$2,$3::jsonb,$4) on conflict(id) do update set metadata=$3::jsonb,state=case when $4<>'Clean' then $4 when studio_intake_attachments.local_attachment_id is not null then studio_intake_attachments.state else $4 end",f.GetProperty("id").GetGuid(),cv,f.GetRawText(),Text(f,"state","Quarantined"));
        WorkflowStore.Sql(c,tx,"update studio_attachments a set storage_status='Quarantined' from studio_intake_attachments i where i.conversation_id=$1 and i.local_attachment_id=a.id and i.state in ('Quarantined','Rejected')",cv);
        if(p.TryGetProperty("paymentEvents",out var events))foreach(var e in events.EnumerateArray())ImportPayment(c,tx,source,cv,e);
        if(!conversation.HasValue)WorkflowStore.Sql(c,tx,"insert into studio_intake_activity(conversation_id) values($1) on conflict do nothing",cv);
        QueueAcknowledgment(c,tx,cv);tx.Commit();return cv;
    }
    static void ImportPayment(NpgsqlConnection c,NpgsqlTransaction tx,string source,Guid cv,JsonElement e)
    {
        var job=WorkflowStore.Scalar(c,tx,"select job_id from studio_conversations where id=$1",cv);if(job is not Guid jobId)throw new ArgumentException("Payment requires a booking job.");
        string id=Text(e,"eventId"),hash=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(e.GetRawText()))),kind=Text(e,"kind");
        long amount=e.GetProperty("amountMinor").GetInt64();if(string.IsNullOrWhiteSpace(id)||amount<=0||amount>99_999_999_900||Text(e,"currency")!="JMD"||kind is not ("Deposit" or "Refund" or "Dispute"))throw new ArgumentException("Invalid verified payment event.");
        string provider=Text(e,"provider"),transaction=Text(e,"transactionReference");
        if(string.IsNullOrWhiteSpace(provider)||string.IsNullOrWhiteSpace(transaction))throw new ArgumentException("Provider and transaction reference required.");
        WorkflowStore.Sql(c,tx,"select pg_advisory_xact_lock(hashtextextended($1,0))","external-payment:"+provider+":"+transaction);
        var same=WorkflowStore.Scalar(c,tx,"select payload::text from studio_external_payment_events where source=$1 and payload->>'provider'=$2 and payload->>'transactionReference'=$3 and payload->>'kind'=$4",source,provider,transaction,kind) as string;
        if(same is not null)
        {
            var recorded=JsonSerializer.Deserialize<JsonElement>(same);
            if(recorded.GetProperty("amountMinor").GetInt64()!=amount||Text(recorded,"currency")!="JMD")throw new ArgumentException("Provider transaction conflicts with a previous financial event.");
            var originalJob=WorkflowStore.Scalar(c,tx,"select job_id from studio_external_payment_events where source=$1 and payload->>'provider'=$2 and payload->>'transactionReference'=$3 and payload->>'kind'=$4",source,provider,transaction,kind);
            if(originalJob is not Guid priorJob||priorJob!=jobId)throw new ArgumentException("Transaction already belongs to different work.");
            return;
        }
        var old=WorkflowStore.Scalar(c,tx,"select fingerprint from studio_external_payment_events where source=$1 and event_id=$2",source,id) as string;
        if(old is not null){if(old!=hash)throw new ArgumentException("Payment event identity conflict.");return;}
        WorkflowStore.Sql(c,tx,"insert into studio_external_payment_events values($1,$2,$3,$4,$5::jsonb,now())",source,id,hash,jobId,e.GetRawText());
        // Existing financial ledger owns balances. Refund/dispute events remain distinct signed entries.
        WorkflowStore.Sql(c,tx,"insert into studio_financial_events(id,job_id,kind,amount,note,recorded_by,created_at_utc) values($1,$2,$3,$4,$5,'Verified website payment',now())",Guid.NewGuid(),jobId,kind=="Dispute"?"Reversal":kind,kind=="Deposit"?amount/100m:-amount/100m,Text(e,"provider")+" / "+Text(e,"transactionReference"));
        if(kind=="Deposit")WorkflowStore.Sql(c,tx,"insert into studio_frontdesk_deposits(id,request_id,amount,currency,provider,reference,recorded_by) select $1,r.id,$2,'JMD',$3,$4,'Verified website payment' from studio_frontdesk_requests r where r.job_id=$5 on conflict(provider,reference) do nothing",Guid.NewGuid(),amount/100m,source+":"+Text(e,"provider"),id,jobId);
    }
    public static void QueueAcknowledgment(NpgsqlConnection c,NpgsqlTransaction tx,Guid cv)
    {
        WorkflowStore.Sql(c,tx,"""
          insert into studio_outbound_email(id,conversation_id,from_address,to_address,subject,body,status,kind,created_at_utc)
          select gen_random_uuid(),v.id,w.email_address,v.contact_address,'Received — '||v.intake_reference,
          'Hello '||v.contact_name||E',\n\nReceived — awaiting review. Reference: '||v.intake_reference||E'\nRequest: '||v.subject||E'\nOur Front Desk team will review your request and contact you. Receipt does not confirm availability, payment or booking.',
          'Pending','Acknowledgement',now() from studio_conversations v join studio_workspaces w on w.id=v.workspace_id
          where v.id=$1 and v.contact_address like '%@%' and nullif(w.email_address,'') is not null
          and not exists(select 1 from studio_outbound_email e where e.conversation_id=v.id and e.kind='Acknowledgement')
          """,cv);
    }
}
public sealed class WebsiteIntakeWorker(StudioDatabase database,WorkspaceStore workspaces,IConfiguration configuration,ILogger<WebsiteIntakeWorker> logger,Storage.AttachmentStore attachments,Notifications.NotificationService notifications):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if(!configuration.GetValue<bool>("PublicSite:PollingEnabled"))return;
        using var site=new PublicSiteClient(configuration);string source=new Uri(configuration["PublicSite:BaseUrl"]!).GetLeftPart(UriPartial.Authority);
        while(!ct.IsCancellationRequested)
        {
            try
            {
                using var c=database.OpenConnection();
                using var gate=WorkflowStore.Command(c,null,"select pg_try_advisory_lock(hashtextextended($1,0))","public-site-poll:"+source);
                if((bool)gate.ExecuteScalar()!)try
                {
                    string cursor=WorkflowStore.Scalar(c,null,"select cursor from studio_website_cursor where source=$1",source) as string??"";
                    var feed=await site.Get("/api/public-site/v1/changes?cursor="+Uri.EscapeDataString(cursor),ct);
                    var importer=new WebsiteIntakeStore(database,workspaces,configuration);
                    var retries=new List<JsonElement>();
                    using(var q=WorkflowStore.Command(c,null,"select payload::text from studio_website_import_errors where source=$1 and payload is not null order by updated_at_utc limit 25",source))using(var r=q.ExecuteReader())while(r.Read())retries.Add(JsonSerializer.Deserialize<JsonElement>(r.GetString(0)));
                    var batch=retries.Concat(feed.GetProperty("items").EnumerateArray()).GroupBy(p=>p.GetProperty("id").GetGuid()).Select(g=>g.OrderByDescending(p=>p.GetProperty("revision").GetInt64()).First());
                    foreach(var p in batch)
                    {
                      try
                      {
                        Guid cv=importer.Import(source,p);
                        // Only approved API IDs are fetched; unavailable files do not prevent metadata import.
                        await site.Post($"/api/public-site/v1/intakes/{p.GetProperty("id").GetGuid():D}/ack",new{revision=p.GetProperty("revision").GetInt64()},ct);
                        logger.LogInformation("Website intake imported. ConversationId={ConversationId} Reference={IntakeReference} Revision={Revision}",cv,WebsiteIntakeStore.Text(p,"intakeReference"),p.GetProperty("revision").GetInt64());
                        WorkflowStore.Sql(c,null,"delete from studio_website_import_errors where source=$1 and external_id=$2",source,p.GetProperty("id").GetGuid());
                      }
                      catch(Exception ex)when(ex is not OperationCanceledException)
                      {
                        WorkflowStore.Sql(c,null,"insert into studio_website_import_errors(source,external_id,revision,error_type,payload) values($1,$2,$3,$4,$5::jsonb) on conflict(source,external_id) do update set revision=$3,error_type=$4,payload=$5::jsonb,updated_at_utc=now()",source,p.GetProperty("id").GetGuid(),p.GetProperty("revision").GetInt64(),ex.GetType().Name,p.GetRawText());
                        logger.LogError("Website intake held for retry. ExternalId={ExternalId} Revision={Revision} ErrorType={ErrorType}",p.GetProperty("id"),p.GetProperty("revision"),ex.GetType().Name);
                      }
                    }
                    WorkflowStore.Sql(c,null,"insert into studio_website_cursor values($1,$2) on conflict(source) do update set cursor=$2",source,feed.GetProperty("nextCursor").GetString()!);
                    var pendingFiles=new List<(Guid Cv,JsonElement Metadata)>();
                    using(var q=WorkflowStore.Command(c,null,"select a.conversation_id,a.metadata::text from studio_intake_attachments a join studio_website_imports i on i.conversation_id=a.conversation_id where i.source=$1 and (a.local_attachment_id is null or a.state<>'Imported') and a.metadata->>'state'='Clean' limit 50",source))using(var r=q.ExecuteReader())while(r.Read())pendingFiles.Add((r.GetGuid(0),JsonSerializer.Deserialize<JsonElement>(r.GetString(1))));
                    foreach(var f in pendingFiles)await ImportFile(site,c,f.Cv,f.Metadata,source,ct);
                    var activities=new List<Guid>();using(var q=WorkflowStore.Command(c,null,"select conversation_id from studio_intake_activity where not published order by created_at_utc limit 100"))using(var r=q.ExecuteReader())while(r.Read())activities.Add(r.GetGuid(0));
                    foreach(var cv in activities){notifications.PublishToAll("WorkspaceMessage","New website intake","A new website inquiry or booking is ready for review.","Conversation",cv.ToString("D"));WorkflowStore.Sql(c,null,"update studio_intake_activity set published=true where conversation_id=$1",cv);}
                    var statuses=new List<(Guid External,long Revision,string Booking,string Ack)>();
                    using(var q=WorkflowStore.Command(c,null,"select i.external_id,i.revision,coalesce(j.status,'Inquiry'),coalesce(e.status,'NotQueued') from studio_website_imports i join studio_conversations v on v.id=i.conversation_id left join studio_jobs j on j.id=v.job_id left join lateral(select status from studio_outbound_email where conversation_id=v.id and kind='Acknowledgement' order by created_at_utc limit 1)e on true where i.source=$1 and coalesce(i.last_status,'')<>coalesce(j.status,'Inquiry')||'|'||coalesce(e.status,'NotQueued') order by i.external_id limit 50",source))using(var r=q.ExecuteReader())while(r.Read())statuses.Add((r.GetGuid(0),r.GetInt64(1),r.GetString(2),r.GetString(3)));
                    foreach(var s in statuses)
                    {
                        await site.Post($"/api/public-site/v1/intakes/{s.External:D}/ack",new{revision=s.Revision,bookingStatus=s.Booking,reviewStatus=s.Booking is "Pending" or "Inquiry"?"AwaitingReview":s.Booking,acknowledgmentStatus=s.Ack},ct);
                        WorkflowStore.Sql(c,null,"update studio_website_imports set last_status=$3 where source=$1 and external_id=$2",source,s.External,s.Booking+"|"+s.Ack);
                    }
                }finally{WorkflowStore.Sql(c,null,"select pg_advisory_unlock(hashtextextended($1,0))","public-site-poll:"+source);}
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception ex){logger.LogError("Website polling cycle incomplete; durable retry state retained. ErrorType={ErrorType}",ex.GetType().Name);}
            try{await Task.Delay(TimeSpan.FromSeconds(60),ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
        }
    }
    async Task ImportFile(PublicSiteClient site,NpgsqlConnection c,Guid cv,JsonElement f,string source,CancellationToken ct)
    {
        Guid id=f.GetProperty("id").GetGuid();
        try
        {
            var bytes=await site.File(id,ct);
            if(!Convert.ToHexString(SHA256.HashData(bytes)).Equals(WebsiteIntakeStore.Text(f,"sha256"),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Attachment hash mismatch.");
            string name=WebsiteIntakeStore.Text(f,"fileName"),type=WebsiteIntakeStore.Text(f,"contentType");
            if(!await WebsiteAttachmentSafety.Scan(bytes,type,name,configuration,ct)){WorkflowStore.Sql(c,null,"update studio_intake_attachments set state='Quarantined' where id=$1",id);return;}
            var saved=await attachments.AddBytesAsync(cv,name,type,bytes,"External","Website",ct,"website:"+source+":"+id);
            WorkflowStore.Sql(c,null,"update studio_attachments set storage_status='Stored' where id=$1",saved.Id);
            WorkflowStore.Sql(c,null,"update studio_intake_attachments set local_attachment_id=$2,state='Imported' where id=$1",id,saved.Id);
        }
        catch(Exception ex)when(ex is not OperationCanceledException)
        {logger.LogWarning("Website attachment held for retry. AttachmentId={AttachmentId} ErrorType={ErrorType}",id,ex.GetType().Name);WorkflowStore.Sql(c,null,"update studio_intake_attachments set state='ImportFailed' where id=$1",id);}
    }

}
