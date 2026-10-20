using System.Security.Claims;
using System.Text.Json;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.FrontDesk;
using Common.Messaging;
using Npgsql;
namespace Aegis.Studio.Web.Reminders;

public sealed class ClientReminderStore(StudioDatabase database,IConfiguration configuration,IEnumerable<IExternalMessageChannel> channels)
{
    public void EnsureSchema()
    {
        using var input=typeof(ClientReminderStore).Assembly.GetManifestResourceStream("Aegis.Studio.Web.Reminders.ClientReminders.sql")!;
        using var reader=new StreamReader(input);using var c=database.OpenConnection();Exec(c,null,reader.ReadToEnd());
    }
    static void Exec(NpgsqlConnection c,NpgsqlTransaction? tx,string sql,params object?[] values)
    {using var q=new NpgsqlCommand(sql,c,tx);foreach(var v in values)q.Parameters.AddWithValue(v??DBNull.Value);q.ExecuteNonQuery();}
    static T Scalar<T>(NpgsqlConnection c,NpgsqlTransaction? tx,string sql,params object?[] values)
    {using var q=new NpgsqlCommand(sql,c,tx);foreach(var v in values)q.Parameters.AddWithValue(v??DBNull.Value);return (T)q.ExecuteScalar()!;}
    public static string Actor(ClaimsPrincipal user)=>user.FindFirstValue("login_name")??user.FindFirstValue(ClaimTypes.NameIdentifier)??throw new UnauthorizedAccessException();
    public IReadOnlyList<ReminderSource> Sources(Guid job)
    {using var c=database.OpenConnection();return Sources(c,null,job);}
    static List<ReminderSource> Sources(NpgsqlConnection c,NpgsqlTransaction? tx,Guid job)
    {
        using var q=new NpgsqlCommand("select kind,source_id,anchor_at_utc,detail,location from studio_client_reminder_sources where job_id=$1 and exists(select 1 from studio_jobs j where j.id=$1 and j.status not in ('Completed','Cancelled','Delivered','Archived')) order by anchor_at_utc",c,tx);q.Parameters.AddWithValue(job);
        using var r=q.ExecuteReader();var values=new List<ReminderSource>();while(r.Read())values.Add(new(r.GetString(0),r.GetGuid(1),r.GetFieldValue<DateTimeOffset>(2),r.GetString(3),r.GetString(4)));return values;
    }
    public object Preview(Guid job)
    {using var c=database.OpenConnection();var contact=ContactFor(c,null,job);return new{client=contact.Client,job=contact.Job,email=contact.Email,phone=contact.Phone,timeZone=configuration["ClientReminders:TimeZone"]??"America/Jamaica"};}
    public object[] History(Guid job,Guid id)
    {
        using var c=database.OpenConnection();using var q=new NpgsqlCommand("select h.recorded_at_utc,h.snapshot::text,h.actor from studio_client_reminder_history h join studio_client_reminders r on r.id=h.reminder_id where r.job_id=$1 and r.id=$2 order by h.id desc limit 100",c);q.Parameters.AddWithValue(job);q.Parameters.AddWithValue(id);using var r=q.ExecuteReader();var values=new List<object>();while(r.Read())values.Add(new{at=r.GetFieldValue<DateTimeOffset>(0),snapshot=JsonSerializer.Deserialize<JsonElement>(r.GetString(1)),actor=r.GetString(2)});return values.ToArray();
    }
    public string KindOf(Guid job,Guid id)
    {using var c=database.OpenConnection();return Scalar<string>(c,null,"select kind from studio_client_reminders where job_id=$1 and id=$2",job,id)??throw new KeyNotFoundException();}
    public object Preferences(Guid job)
    {using var c=database.OpenConnection();using var q=new NpgsqlCommand("select email_allowed,whatsapp_allowed from studio_client_reminder_preferences where job_id=$1",c);q.Parameters.AddWithValue(job);using var r=q.ExecuteReader();return r.Read()?new ReminderPreferences(r.GetBoolean(0),r.GetBoolean(1)):new ReminderPreferences(false,false);}
    public void SavePreferences(Guid job,ReminderPreferences p,string actor)
    {using var c=database.OpenConnection();Exec(c,null,"insert into studio_client_reminder_preferences(job_id,email_allowed,whatsapp_allowed,updated_by) values($1,$2,$3,$4) on conflict(job_id) do update set email_allowed=$2,whatsapp_allowed=$3,updated_by=$4,updated_at_utc=now()",job,p.EmailAllowed,p.WhatsAppAllowed,actor);}
    public ReminderRuleRequest[] Rules(Guid job)
    {
        using var c=database.OpenConnection();using var q=new NpgsqlCommand("select kind,enabled,channel,offset_hours,subject,body from studio_client_reminder_rules where job_id=$1",c);
        q.Parameters.AddWithValue(job);using var r=q.ExecuteReader();var list=new List<ReminderRuleRequest>();
        while(r.Read())list.Add(new(r.GetString(0),r.GetBoolean(1),r.GetString(2),r.GetInt32(3),r.GetString(4),r.GetString(5)));
        return list.ToArray();
    }

    public void SaveRule(Guid job,ReminderRuleRequest rule,string actor)
    {
        ClientReminderRules.Validate(rule.Kind,rule.Channel,rule.Subject,rule.Body);
        if(rule.Kind=="FollowUp"||Math.Abs((long)rule.OffsetHours)>720||rule.Kind=="Shoot"&&rule.OffsetHours>0||rule.Kind!="Shoot"&&rule.OffsetHours<0)throw new ArgumentException("Use up to 720 hours before shoots or after other events.");
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        Exec(c,tx,"select set_config('studio.reminder_actor',$1,true)",actor);
        Exec(c,tx,"insert into studio_client_reminder_rules(job_id,kind,enabled,channel,offset_hours,subject,body,created_by) values($1,$2,$3,$4,$5,$6,$7,$8) on conflict(job_id,kind) do update set enabled=$3,channel=$4,offset_hours=$5,subject=$6,body=$7,created_by=$8",job,rule.Kind,rule.Enabled,rule.Channel,rule.OffsetHours,rule.Subject,rule.Body,actor);
        if(!rule.Enabled){Exec(c,tx,"update studio_client_reminders set status='Cancelled',last_error='Automatic rule disabled',updated_at_utc=now() where job_id=$1 and kind=$2 and automatic and status in ('Scheduled','Queued')",job,rule.Kind);}
        tx.Commit();
    }
    public object[] List(Guid? job,ClaimsPrincipal user,JobAccessService access)
    {
        var permissions=access.GetPermissionMap(user);var allowed=permissions.Where(p=>p.Value.ViewConversation).Select(p=>p.Key).ToArray();using var c=database.OpenConnection();
        using var q=new NpgsqlCommand("""
          select r.id,r.job_id,j.title,r.kind,r.channel,r.recipient,r.scheduled_at_utc,r.subject,r.body,
          r.status,r.last_error,r.automatic,r.created_at_utc,e.status,e.last_error,e.provider_accepted_at_utc
          from studio_client_reminders r join studio_jobs j on j.id=r.job_id left join studio_outbound_email e on e.id=r.message_id
          where r.job_id=any($1) and ($2::uuid is null or r.job_id=$2) order by case when r.status in ('Failed','Attention') then 0 when r.status='Scheduled' then 1 when r.status in ('Queued','Sending') then 2 else 3 end,r.scheduled_at_utc limit 500
          """,c);q.Parameters.AddWithValue(allowed);q.Parameters.AddWithValue((object?)job??DBNull.Value);using var r=q.ExecuteReader();var list=new List<object>();
        while(r.Read()){if(r.GetString(3)=="Deposit"&&!access.HasPermission(user,r.GetGuid(1),"ViewFinancials"))continue;list.Add(new{id=r.GetGuid(0),jobId=r.GetGuid(1),jobTitle=r.GetString(2),kind=r.GetString(3),channel=r.GetString(4),recipient=r.GetString(5),scheduledAtUtc=r.GetFieldValue<DateTimeOffset>(6),subject=r.GetString(7),body=r.GetString(8),status=r.GetString(9),error=r.IsDBNull(10)?null:r.GetString(10),automatic=r.GetBoolean(11),createdAtUtc=r.GetFieldValue<DateTimeOffset>(12),deliveryStatus=r.IsDBNull(13)?null:r.GetString(13),deliveryError=r.IsDBNull(14)?null:r.GetString(14),providerAcceptedAtUtc=r.IsDBNull(15)?(DateTimeOffset?)null:r.GetFieldValue<DateTimeOffset>(15),canManage=access.HasPermission(user,r.GetGuid(1),"ReplyRequester")});}return list.ToArray();
    }
    sealed record Contact(Guid Conversation,string Client,string Job,string? Email,string? Phone,string? From);
    static Contact ContactFor(NpgsqlConnection c,NpgsqlTransaction? tx,Guid job)
    {
        using var q=new NpgsqlCommand("""
          select j.conversation_id,j.client_name,j.title,
          coalesce(nullif(cli.email,''),b.payload->>'clientEmail',case when cv.contact_address like '%@%' then cv.contact_address end),
          coalesce(nullif(cli.phone,''),b.payload->>'clientPhone'),w.email_address
          from studio_jobs j join studio_conversations cv on cv.id=j.conversation_id join studio_workspaces w on w.id=j.workspace_id
          left join studio_clients cli on cli.id=j.client_id
          left join lateral(select payload from studio_frontdesk_requests where job_id=j.id order by created_at_utc desc limit 1)b on true where j.id=$1
          """,c,tx);q.Parameters.AddWithValue(job);using var r=q.ExecuteReader();if(!r.Read())throw new KeyNotFoundException("Job was not found.");return new(r.GetGuid(0),r.GetString(1),r.GetString(2),r.IsDBNull(3)?null:r.GetString(3),r.IsDBNull(4)?null:r.GetString(4),r.IsDBNull(5)?null:r.GetString(5));
    }
    static string Recipient(Contact c,string channel)=>channel=="WhatsApp"?DepositDeliveryRules.Mobile(c.Phone):System.Net.Mail.MailAddress.TryCreate(c.Email,out var email)?email.Address:throw new ArgumentException("A valid client email is required.");
    void CheckChannel(NpgsqlConnection c,NpgsqlTransaction? tx,Guid job,string channel)
    {
        if(!Scalar<bool>(c,tx,"select exists(select 1 from studio_client_reminder_preferences where job_id=$1 and case when $2='Email' then email_allowed else whatsapp_allowed end)",job,channel))throw new ArgumentException("Enable this channel only after confirming the client's communication preference.");
        var kind=channel=="Email"?MessageChannel.MsEmail:MessageChannel.WhatsApp;
        if(!channels.Any(x=>x.Channel==kind))throw new ArgumentException($"Configure the {channel} provider before sending reminders.");
        if(channel=="WhatsApp"&&string.IsNullOrWhiteSpace(configuration["Messaging:WhatsApp:ReminderTemplateName"]))throw new ArgumentException("Configure an approved WhatsApp reminder template before scheduling reminders.");
    }
    public Guid Create(Guid job,ReminderRequest request,string actor)
    {
        if(request.Id==Guid.Empty)throw new ArgumentException("A stable request ID is required.");
        ClientReminderRules.Validate(request.Kind,request.Channel,request.Subject,request.Body);
        if(request.ScheduledAtUtc>DateTimeOffset.UtcNow.AddYears(1))throw new ArgumentException("Schedule within the next year.");
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        // Serialize retries of the same operation across processes.
        Exec(c,tx,"select pg_advisory_xact_lock(hashtextextended($1,0))",request.Id.ToString("D"));
        // Repeating the same HTTP request returns its original reminder without another send.
        if(Scalar<bool>(c,tx,"select exists(select 1 from studio_client_reminders where id=$1)",request.Id))
        {if(!Scalar<bool>(c,tx,"select exists(select 1 from studio_client_reminders where id=$1 and job_id=$2 and created_by=$3)",request.Id,job,actor))throw new ArgumentException("This request ID is already in use.");return request.Id;}
        var source=request.Kind=="FollowUp"?new ReminderSource("FollowUp",job,DateTimeOffset.UtcNow,"",""):Sources(c,tx,job).FirstOrDefault(s=>s.Kind==request.Kind&&(!request.SourceId.HasValue||s.SourceId==request.SourceId))??throw new ArgumentException("This client action is no longer outstanding.");
        Insert(c,tx,job,request.Id,source,request.Channel,request.ScheduledAtUtc,request.Subject,request.Body,actor,false);tx.Commit();return request.Id;
    }
    void Insert(NpgsqlConnection c,NpgsqlTransaction tx,Guid job,Guid id,ReminderSource source,string channel,DateTimeOffset due,string subject,string body,string actor,bool automatic)
    {
        CheckChannel(c,tx,job,channel);var contact=ContactFor(c,tx,job);var recipient=Recipient(contact,channel);
        if(channel=="Email"&&string.IsNullOrWhiteSpace(contact.From))throw new ArgumentException("Configure the workspace email identity first.");
        if(!Scalar<bool>(c,tx,"select studio_client_reminder_needed($1,$2,$3,$4)",job,source.Kind,source.SourceId,source.AnchorAtUtc))throw new ArgumentException("The client action is no longer outstanding.");
        Exec(c,tx,"""
          insert into studio_client_reminders(id,job_id,kind,source_id,anchor_at_utc,scheduled_at_utc,channel,recipient,subject,body,created_by,automatic,source_detail,source_location)
          values($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14) on conflict do nothing
          """,id,job,source.Kind,source.SourceId,source.AnchorAtUtc,due.ToUniversalTime(),channel,recipient,ClientReminderRules.Render(subject,contact.Client,contact.Job,source.Detail,source.Location),ClientReminderRules.Render(body,contact.Client,contact.Job,source.Detail,source.Location),actor,automatic,source.Detail,source.Location);
    }
    public void Change(Guid job,Guid id,ReminderChange change,string actor)
    {
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        Exec(c,tx,"select set_config('studio.reminder_actor',$1,true)",actor);
        string status=Scalar<string>(c,tx,"select status from studio_client_reminders where id=$1 and job_id=$2 for update",id,job)??throw new KeyNotFoundException();
        string? delivery=Scalar<string>(c,tx,"select status from studio_outbound_email where id=$1 for update",id);
        if(delivery is not (null or "Pending" or "Retry" or "Cancelled"))throw new ArgumentException("Delivery has started. Review its provider status before another send.");
        if(change.Action=="edit")
        {
            if(status!="Scheduled")throw new ArgumentException("Only scheduled reminders can be edited.");
            string kind=Scalar<string>(c,tx,"select kind from studio_client_reminders where id=$1",id),channel=Scalar<string>(c,tx,"select channel from studio_client_reminders where id=$1",id);
            ClientReminderRules.Validate(kind,channel,change.Subject??"",change.Body??"");
            var contact=ContactFor(c,tx,job);var sourceId=Scalar<Guid>(c,tx,"select source_id from studio_client_reminders where id=$1",id);var source=Sources(c,tx,job).FirstOrDefault(s=>s.Kind==kind&&s.SourceId==sourceId);
            Exec(c,tx,"update studio_client_reminders set subject=$2,body=$3,updated_at_utc=now() where id=$1",id,ClientReminderRules.Render(change.Subject!,contact.Client,contact.Job,source?.Detail??"",source?.Location??""),ClientReminderRules.Render(change.Body!,contact.Client,contact.Job,source?.Detail??"",source?.Location??""));
        }
        else if(change.Action=="cancel")Exec(c,tx,"update studio_client_reminders set status='Cancelled',updated_at_utc=now() where id=$1 and status in ('Scheduled','Queued','Failed')",id);
        else if(change.Action is "send" or "postpone" or "retry")
        {
            if(status is not ("Scheduled" or "Failed"))throw new ArgumentException("This reminder has already entered delivery. Check its delivery history before another send.");
            if(status=="Failed")
            {string channel=Scalar<string>(c,tx,"select channel from studio_client_reminders where id=$1",id);CheckChannel(c,tx,job,channel);Exec(c,tx,"update studio_client_reminders set recipient=$2 where id=$1",id,Recipient(ContactFor(c,tx,job),channel));}
            var due=change.Action=="postpone"?change.ScheduledAtUtc??throw new ArgumentException("Choose a new time."):DateTimeOffset.UtcNow;
            if(due>DateTimeOffset.UtcNow.AddYears(1))throw new ArgumentException("Schedule within the next year.");
            Exec(c,tx,"update studio_client_reminders set status='Scheduled',scheduled_at_utc=$2,last_error=null,updated_at_utc=now() where id=$1 and message_id is null",id,due.ToUniversalTime());
        }else throw new ArgumentException("Choose send, postpone, cancel or retry.");
        Exec(c,tx,"update studio_outbound_email set status='Cancelled',last_error='Reminder cancelled' where id=$1 and status in ('Pending','Retry') and exists(select 1 from studio_client_reminders r where r.message_id=$1 and r.status='Cancelled')",id);tx.Commit();
    }
    public void Tick()
    {
        using var maintenance=DemoMaintenance.TryEnterOperation(database);if(maintenance is null)return;
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        if(!Scalar<bool>(c,tx,"select pg_try_advisory_xact_lock(72919347)"))return;
        Exec(c,tx,"select set_config('studio.reminder_actor','System',true)");
        Exec(c,tx,"update studio_client_reminders r set status=case e.status when 'ProviderAccepted' then 'Submitted' else e.status end,updated_at_utc=now() from studio_outbound_email e where r.message_id=e.id and r.status in ('Queued','Sending') and e.status in ('ProviderAccepted','Sending','Attention','Failed') and r.status<>case e.status when 'ProviderAccepted' then 'Submitted' else e.status end");
        Exec(c,tx,"update studio_client_reminders set status='Cancelled',last_error='Client action completed, event changed or contact permission withdrawn',updated_at_utc=now() where status in ('Scheduled','Queued') and (not studio_client_reminder_current(id) or (subject_id is null and not exists(select 1 from studio_client_reminder_preferences p where p.job_id=studio_client_reminders.job_id and case when channel='Email' then p.email_allowed else p.whatsapp_allowed end)))");
        Exec(c,tx,"update studio_client_reminders set status='Cancelled',last_error='Client contact changed; create a new reminder',updated_at_utc=now() where status='Queued' and not studio_client_reminder_dispatchable(message_id) and exists(select 1 from studio_outbound_email e where e.id=message_id and e.status in ('Pending','Retry'))");
        Exec(c,tx,"update studio_outbound_email e set status='Cancelled',last_error='Client reminder cancelled' from studio_client_reminders r where r.message_id=e.id and r.status='Cancelled' and e.status in ('Pending','Retry')");
        var rules=new List<(Guid Job,ReminderRuleRequest Rule,string Actor)>();
        using(var q=new NpgsqlCommand("select job_id,kind,channel,offset_hours,subject,body,created_by from studio_client_reminder_rules where enabled",c,tx))using(var r=q.ExecuteReader())while(r.Read())rules.Add((r.GetGuid(0),new(r.GetString(1),true,r.GetString(2),r.GetInt32(3),r.GetString(4),r.GetString(5)),r.GetString(6)));
        foreach(var rule in rules)
        foreach(var source in Sources(c,tx,rule.Job).Where(s=>s.Kind==rule.Rule.Kind))
        {
            if(Scalar<bool>(c,tx,"select exists(select 1 from studio_client_reminders where job_id=$1 and kind=$2 and source_id=$3 and anchor_at_utc=$4 and automatic)",rule.Job,source.Kind,source.SourceId,source.AnchorAtUtc))continue;
            if(Scalar<long>(c,tx,"select count(*) from studio_client_reminders where job_id=$1 and kind=$2 and automatic and created_at_utc>now()-interval '7 days'",rule.Job,source.Kind)>=3)continue;
            // Savepoints keep a single invalid contact/provider from blocking unrelated jobs.
            tx.Save("reminder_rule");
            try{Insert(c,tx,rule.Job,Guid.NewGuid(),source,rule.Rule.Channel,source.AnchorAtUtc.AddHours(rule.Rule.OffsetHours),rule.Rule.Subject,rule.Rule.Body,rule.Actor,true);}
            catch(ArgumentException e)
            {
                tx.Rollback("reminder_rule");
                var contact=ContactFor(c,tx,rule.Job);
                Exec(c,tx,"insert into studio_client_reminders(id,job_id,kind,source_id,anchor_at_utc,scheduled_at_utc,channel,recipient,subject,body,created_by,automatic,status,last_error,source_detail,source_location) values($1,$2,$3,$4,$5,now(),$6,$7,$8,$9,$10,true,'Failed',$11,$12,$13) on conflict do nothing",Guid.NewGuid(),rule.Job,source.Kind,source.SourceId,source.AnchorAtUtc,rule.Rule.Channel,rule.Rule.Channel=="Email"?contact.Email??"":contact.Phone??"",ClientReminderRules.Render(rule.Rule.Subject,contact.Client,contact.Job,source.Detail,source.Location),ClientReminderRules.Render(rule.Rule.Body,contact.Client,contact.Job,source.Detail,source.Location),rule.Actor,e.Message,source.Detail,source.Location);
            }
        }
        ScheduleSubjects(c,tx);
        var dueIds=new List<Guid>();using(var q=new NpgsqlCommand("select id from studio_client_reminders where status='Scheduled' and scheduled_at_utc<=now() order by scheduled_at_utc limit 100",c,tx))using(var r=q.ExecuteReader())while(r.Read())dueIds.Add(r.GetGuid(0));
        var zone=TimeZoneInfo.FindSystemTimeZoneById(configuration["ClientReminders:TimeZone"]??"America/Jamaica");
        if(!ClientReminderRules.IsQuiet(DateTimeOffset.UtcNow,zone))foreach(var id in dueIds)
        {
            tx.Save("reminder_send");try{Queue(c,tx,id);}catch(ArgumentException e){tx.Rollback("reminder_send");Exec(c,tx,"update studio_client_reminders set status='Failed',last_error=$2,updated_at_utc=now() where id=$1",id,e.Message);}
        }
        tx.Commit();
    }
    void ScheduleSubjects(NpgsqlConnection c,NpgsqlTransaction tx)
    {
        // Subject reminders reuse the existing scheduler, delivery guard, outbox and audit history.
        foreach(string channel in new[]{"Email","WhatsApp"})
        {
            var transport=channel=="Email"?MessageChannel.MsEmail:MessageChannel.WhatsApp;
            if(!channels.Any(x=>x.Channel==transport)||channel=="WhatsApp"&&string.IsNullOrWhiteSpace(configuration["Messaging:WhatsApp:ReminderTemplateName"]))continue;
            Exec(c,tx,"""
              insert into studio_client_reminders(id,job_id,kind,source_id,anchor_at_utc,scheduled_at_utc,channel,recipient,subject,body,created_by,automatic,subject_id)
              select gen_random_uuid(),s.job_id,'Collection',s.id,s.pickup_ready_at_utc,now(),$1,
               case when $1='Email' then s.contact_email else regexp_replace(s.contact_phone,'[+ ()\-]','','g') end,
               'Ready for pickup: '||j.title,'Hello '||coalesce(nullif(s.contact_name,''),s.name)||', photographs for '||s.name||' are ready for pickup. Please contact Brian''s to arrange collection.',
               'Subject pickup preference',true,s.id
              from studio_job_subjects s join studio_jobs j on j.id=s.job_id where not s.is_system and s.production_status='Needs Delivery'
               and s.pickup_ready_at_utc is not null and s.pickup_notification=$1 and j.status not in ('Cancelled','Archived','Delivered','Completed')
               and case when $1='Email' then s.contact_email like '%@%' else s.contact_phone ~ '^\+[0-9]{8,15}$' end
               and not exists(select 1 from studio_job_fulfillment f where f.job_id=j.id)
              on conflict do nothing
              """,channel);
        }
    }
    void Queue(NpgsqlConnection c,NpgsqlTransaction tx,Guid id)
    {
        Guid job;string channel,recipient,subject,body,actor;
        using(var q=new NpgsqlCommand("select job_id,channel,recipient,subject,body,created_by from studio_client_reminders where id=$1 for update",c,tx))
        {q.Parameters.AddWithValue(id);using var r=q.ExecuteReader();r.Read();job=r.GetGuid(0);channel=r.GetString(1);recipient=r.GetString(2);subject=r.GetString(3);body=r.GetString(4);actor=r.GetString(5);}
        Guid? subjectId=Scalar<object>(c,tx,"select subject_id from studio_client_reminders where id=$1",id) is Guid sid?sid:null;
        var contact=ContactFor(c,tx,job);
        if(subjectId.HasValue)
        {
            if(!Scalar<bool>(c,tx,"select studio_subject_reminder_current($1)",id))throw new ArgumentException("Subject readiness, contact or preference changed.");
            using var q=new NpgsqlCommand("select name,contact_email,contact_phone from studio_job_subjects where id=$1 and job_id=$2 and not is_system",c,tx);q.Parameters.AddWithValue(subjectId.Value);q.Parameters.AddWithValue(job);using var r=q.ExecuteReader();if(!r.Read())throw new ArgumentException("Subject unavailable.");contact=contact with{Client=r.GetString(0),Email=r.GetString(1),Phone=r.GetString(2)};
        }
        else CheckChannel(c,tx,job,channel);
        if(channel=="Email"&&string.IsNullOrWhiteSpace(contact.From))throw new ArgumentException("Configure the workspace email identity first.");
        if(Recipient(contact,channel)!=recipient)throw new ArgumentException("Client contact changed. Cancel this reminder and create a new one.");
        string reference="AEGIS-"+contact.Conversation.ToString("N")[..8].ToUpperInvariant();
        subject="["+reference+"] "+subject;body+="\n\nReference: "+reference;
        var metadata=new Dictionary<string,string>{{"ReminderTimeZone",configuration["ClientReminders:TimeZone"]??"America/Jamaica"}};if(channel=="WhatsApp")
        {metadata["WhatsAppTemplateName"]=configuration["Messaging:WhatsApp:ReminderTemplateName"]!;metadata["WhatsAppTemplateLanguage"]=configuration["Messaging:WhatsApp:ReminderTemplateLanguage"]??"en_US";metadata["WhatsAppTemplateParameters"]=body.Replace('|',' ');}
        Exec(c,tx,"""
          insert into studio_outbound_email(id,conversation_id,from_address,to_address,subject,body,status,created_at_utc,kind,sent_by_user_name,sending_identity_type,delivery_channel,delivery_metadata)
          values($1,$2,$3,$4,$5,$6,'Pending',now(),'ClientReminder',$7,$8,$9,$10::jsonb) on conflict(id) do nothing
          """,id,contact.Conversation,channel=="Email"?contact.From!:"Configured WhatsApp provider",recipient,subject,body,actor,channel=="Email"?"Workspace":"Provider",channel,JsonSerializer.Serialize(metadata));
        Exec(c,tx,"update studio_client_reminders set status='Queued',message_id=$1,updated_at_utc=now() where id=$1",id);
    }
}

