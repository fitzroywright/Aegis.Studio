using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aegis.Studio.Web.Finance;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Workflow;
using Common.Secrets;

namespace Aegis.Studio.Web.PublicApi;

public static class PublicWebhookSigning
{
    public static string Sign(string secret, string timestamp, string eventId, string payload)
        => PublicV1Signing.Sign(secret, timestamp + "\n" + eventId + "\n" + payload);
    public static TimeSpan RetryDelay(int attempt) => TimeSpan.FromSeconds(Math.Min(3600, 15 * Math.Pow(2, Math.Clamp(attempt - 1, 0, 8))));
}

public sealed class PublicWebhookWorker(StudioDatabase db, IConfiguration configuration, ISecretProvider secrets, ILogger<PublicWebhookWorker> logger) : BackgroundService
{
    private Guid? availabilityCursor;
    public static void EnsureSchema(StudioDatabase database)
    {
        using var c = database.OpenConnection();
        WorkflowStore.Sql(c, null, """
            create table if not exists studio_public_webhook_events(
              id uuid primary key,event_type text not null,resource_id uuid not null,event_key text not null unique,
              occurred_at_utc timestamptz not null default now(),data jsonb not null);
            create table if not exists studio_public_webhook_deliveries(
              event_id uuid not null references studio_public_webhook_events(id),destination_key text not null,
              attempts integer not null default 0,next_attempt_utc timestamptz not null default now(),
              leased_until_utc timestamptz null,lease_id uuid null,delivered_at_utc timestamptz null,last_error text null,
              primary key(event_id,destination_key));
            create or replace function studio_public_booking_event() returns trigger language plpgsql as $$
            declare kind text; begin
              if current_setting('studio.demo_seed',true)='true' then return new; end if;
              kind:=case when tg_op='INSERT' then 'booking.received' when new.status='Confirmed' and old.status is distinct from new.status then 'booking.confirmed' else null end;
              if kind is not null then insert into studio_public_webhook_events(id,event_type,resource_id,event_key,data)
                values(gen_random_uuid(),kind,new.id,kind||':'||new.id,jsonb_build_object('reference',new.id,'status',case when kind='booking.received' then 'received' else 'confirmed' end)) on conflict(event_key) do nothing; end if;
              return new;
            end $$;
            drop trigger if exists public_booking_event on studio_frontdesk_requests;
            create trigger public_booking_event after insert or update on studio_frontdesk_requests for each row execute function studio_public_booking_event();
            create or replace function studio_public_gallery_event() returns trigger language plpgsql as $$
            declare kind text; begin
              if current_setting('studio.demo_seed',true)='true' then return new; end if;
              kind:=case when exists(select 1 from studio_workflow_publications where job_id=new.job_id and id<>new.id) then 'gallery.updated' else 'gallery.published' end;
              insert into studio_public_webhook_events(id,event_type,resource_id,event_key,data)
                values(gen_random_uuid(),kind,new.job_id,kind||':'||new.id,jsonb_build_object('reference',new.job_id,'publicationId',new.id)) on conflict(event_key) do nothing;
              return new;
            end $$;
            drop trigger if exists public_gallery_event on studio_workflow_publications;
            create trigger public_gallery_event after insert on studio_workflow_publications for each row execute function studio_public_gallery_event();
            """);
    }
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        while (!token.IsCancellationRequested)
        {
            try
            {
                var targets = configuration.GetSection("PublicApi:Webhooks:Targets").GetChildren().ToArray();
                if (targets.Length > 0)
                {
                    QueueFinalAvailability();
                    foreach (var target in targets)
                    {
                        if (!Uri.TryCreate(target["Url"], UriKind.Absolute, out var url) || url.Scheme != "https" || !string.IsNullOrEmpty(url.UserInfo))
                        { logger.LogWarning("Webhook destination {Key} must be a configured HTTPS URL", target.Key); continue; }
                        string? secret = await secrets.GetAsync(target["SecretName"] ?? "PublicApi:Webhooks:" + target.Key + ":Secret", token);
                        if (string.IsNullOrWhiteSpace(secret)) { logger.LogWarning("Webhook signing secret missing for {Key}",target.Key); continue; }
                        using var c = db.OpenConnection();
                        var events = target.GetSection("Events").Get<string[]>() ?? ["booking.received","booking.confirmed","gallery.published","gallery.updated","final.delivery.available"];
                        WorkflowStore.Sql(c, null, "insert into studio_public_webhook_deliveries(event_id,destination_key) select id,$1 from studio_public_webhook_events where event_type=any($2) on conflict do nothing",target.Key,events);
                        for (int count = 0; count < 20 && !token.IsCancellationRequested; count++)
                        {
                            Guid lease = Guid.NewGuid();
                            using var q = WorkflowStore.Command(c, null, """
                                with candidate as(select event_id,destination_key from studio_public_webhook_deliveries
                                  where destination_key=$1 and delivered_at_utc is null and next_attempt_utc<=now() and (leased_until_utc is null or leased_until_utc<now())
                                  order by next_attempt_utc for update skip locked limit 1)
                                update studio_public_webhook_deliveries d set leased_until_utc=now()+interval '1 minute',lease_id=$2,attempts=d.attempts+1
                                from candidate x where d.event_id=x.event_id and d.destination_key=x.destination_key
                                returning d.event_id,d.attempts
                                """,target.Key,lease);
                            Guid id;int attempt;
                            using(var r=q.ExecuteReader()){if(!r.Read())break;id=r.GetGuid(0);attempt=r.GetInt32(1);}
                            string payload=(string)WorkflowStore.Scalar(c,null,"select jsonb_build_object('schemaVersion',1,'id',id,'type',event_type,'occurredAt',occurred_at_utc,'data',data)::text from studio_public_webhook_events where id=$1",id)!;
                            string timestamp=DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
                            using var message=new HttpRequestMessage(HttpMethod.Post,url){Content=new StringContent(payload,Encoding.UTF8,"application/json")};
                            message.Headers.Add("X-Studio-Event-Id",id.ToString("D"));message.Headers.Add("X-Studio-Timestamp",timestamp);
                            message.Headers.Add("X-Studio-Signature",PublicWebhookSigning.Sign(secret,timestamp,id.ToString("D"),payload));
                            string? error=null;
                            try{using var response=await client.SendAsync(message,HttpCompletionOption.ResponseHeadersRead,token);if(!response.IsSuccessStatusCode)error="HTTP "+(int)response.StatusCode;}
                            catch(Exception e) when(!token.IsCancellationRequested){error=e.GetType().Name;}
                            WorkflowStore.Sql(c,null,"""
                                update studio_public_webhook_deliveries set delivered_at_utc=case when $4::text is null then now() else null end,
                                  last_error=$4,next_attempt_utc=$5,leased_until_utc=null,lease_id=null where event_id=$1 and destination_key=$2 and lease_id=$3
                                """,id,target.Key,lease,error,DateTimeOffset.UtcNow+PublicWebhookSigning.RetryDelay(attempt));
                        }
                    }
                }
            }
            catch(Exception e) when(!token.IsCancellationRequested){logger.LogError(e,"Public webhook processing failed; persisted deliveries will retry");}
            try{await Task.Delay(TimeSpan.FromSeconds(15),token);}catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
        }
    }
    private void QueueFinalAvailability()
    {
        using var c=db.OpenConnection();var publications=new List<(Guid Id,Guid Job)>();
        using(var q=WorkflowStore.Command(c,null,"select p.id,p.job_id from studio_workflow_publications p join studio_jobs j on j.id=p.job_id where j.status in ('Ready for Delivery','Delivered','Completed') and not exists(select 1 from studio_public_webhook_events where event_key='final:'||p.id) and ($1::uuid is null or p.id>$1) order by p.id limit 100",availabilityCursor))
        using(var r=q.ExecuteReader())while(r.Read())publications.Add((r.GetGuid(0),r.GetGuid(1)));
        availabilityCursor=publications.Count<100?null:publications[^1].Id;
        foreach(var p in publications)
        {
            using var tx=c.BeginTransaction();WorkflowStore.Scalar(c,tx,"select id from studio_jobs where id=$1 for share",p.Job);
            Guid latest;try{latest=PublicV1Store.Publication(c,tx,p.Job);}catch(PublicApiException){continue;}
            if(latest==p.Id&&FinanceReleaseStore.Decision(c,tx,p.Job).Authorized)
                WorkflowStore.Sql(c,tx,"insert into studio_public_webhook_events(id,event_type,resource_id,event_key,data) values($1,'final.delivery.available',$2,$3,jsonb_build_object('reference',$2,'publicationId',$4)) on conflict(event_key) do nothing",Guid.NewGuid(),p.Job,"final:"+p.Id,p.Id);
            tx.Commit();
        }
    }
}
