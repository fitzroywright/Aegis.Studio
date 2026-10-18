create table if not exists studio_client_reminder_preferences(
 job_id uuid primary key references studio_jobs(id) on delete cascade,
 email_allowed boolean not null default false, whatsapp_allowed boolean not null default false,
 updated_by text not null, updated_at_utc timestamptz not null default now());
create table if not exists studio_client_reminder_rules(
 job_id uuid not null references studio_jobs(id) on delete cascade, kind text not null,
 enabled boolean not null default false, channel text not null, offset_hours integer not null,
 subject text not null, body text not null, created_by text not null,
 primary key(job_id,kind));
create table if not exists studio_client_reminders(
 id uuid primary key,job_id uuid not null references studio_jobs(id) on delete cascade,
 kind text not null,source_id uuid not null,anchor_at_utc timestamptz not null,
 scheduled_at_utc timestamptz not null,channel text not null,recipient text not null,
 subject text not null,body text not null,status text not null default 'Scheduled',
 automatic boolean not null default false,created_by text not null,
 created_at_utc timestamptz not null default now(),updated_at_utc timestamptz not null default now(),
 message_id uuid,last_error text,source_detail text not null default '',source_location text not null default '');
create unique index if not exists ux_client_reminder_event on studio_client_reminders(job_id,kind,source_id,anchor_at_utc) where automatic;
create index if not exists ix_client_reminders_due on studio_client_reminders(scheduled_at_utc) where status='Scheduled';

-- Only explicit client-wait tasks trigger selections/approvals. Internal QC does not.
create or replace view studio_client_reminder_sources as
 select s.job_id,'Shoot'::text kind,s.id source_id,s.call_time_utc anchor_at_utc,
 concat(to_char(s.call_time_utc at time zone 'America/Jamaica','DD Mon YYYY HH24:MI'),' Jamaica time') detail,
 coalesce(s.location,'') AS location from studio_shoot_sessions s
 where s.status not in ('Cancelled','ShootComplete','Completed') and s.call_time_utc>now()
 union all
 select r.job_id,'Deposit',r.id,r.created_at_utc,
 concat(r.payload->>'currency',' ',greatest(0,(r.payload->>'requiredDeposit')::numeric-coalesce((select sum(d.amount) from studio_frontdesk_deposits d where d.request_id=r.id),0))),''
 from studio_frontdesk_requests r where r.job_id is not null and r.status not in ('Cancelled','Confirmed')
 and (r.payload->>'requiredDeposit')::numeric>coalesce((select sum(d.amount) from studio_frontdesk_deposits d where d.request_id=r.id),0)
 union all
 select a.job_id,case when a.title ilike '%approv%' then 'Approval' else 'Selection' end,a.id,a.updated_at_utc,a.title,''
 from studio_job_activities a where a.status='WaitingOnClient'
 union all
 select j.id,'Collection',j.id,coalesce((select max(h.entered_at_utc) from studio_workflow_history h where h.job_id=j.id and h.new_stage='Ready for Delivery'),j.stage_entered_at_utc,j.created_at_utc),j.title,''
 from studio_jobs j where j.status='Ready for Delivery' and not exists(select 1 from studio_job_fulfillment f where f.job_id=j.id);

create or replace function studio_client_reminder_needed(job uuid,reminder_kind text,source uuid,anchor timestamptz)
returns boolean language sql stable as $$
 select exists(select 1 from studio_jobs j where j.id=job and j.status not in ('Completed','Cancelled','Delivered','Archived')
 and (reminder_kind='FollowUp' or exists(select 1 from studio_client_reminder_sources s where s.job_id=j.id and s.kind=reminder_kind and s.source_id=source and s.anchor_at_utc=anchor)))
$$;

create or replace function studio_client_reminder_current(reminder uuid)
returns boolean language sql stable as $$
 select exists(select 1 from studio_client_reminders r where r.id=reminder
 and studio_client_reminder_needed(r.job_id,r.kind,r.source_id,r.anchor_at_utc)
 and (r.kind='FollowUp' or exists(select 1 from studio_client_reminder_sources s where s.job_id=r.job_id and s.kind=r.kind and s.source_id=r.source_id and s.anchor_at_utc=r.anchor_at_utc and s.detail=r.source_detail and s.location=r.source_location)))
$$;

create or replace function studio_client_reminder_dispatchable(message uuid)
returns boolean language sql stable as $$
 select exists(select 1 from studio_client_reminders r
 join studio_client_reminder_preferences p on p.job_id=r.job_id
 join studio_jobs j on j.id=r.job_id join studio_conversations cv on cv.id=j.conversation_id
 left join studio_clients cli on cli.id=j.client_id
 left join lateral(select payload from studio_frontdesk_requests where job_id=j.id order by created_at_utc desc limit 1)b on true
 where r.message_id=message and r.status='Queued'
 and studio_client_reminder_current(r.id)
 and case when r.channel='Email' then p.email_allowed and lower(r.recipient)=lower(coalesce(nullif(cli.email,''),b.payload->>'clientEmail',case when cv.contact_address like '%@%' then cv.contact_address end))
 else p.whatsapp_allowed and r.recipient=regexp_replace(coalesce(nullif(cli.phone,''),b.payload->>'clientPhone'),'[+ ()\-]','','g') end)
$$;

create table if not exists studio_client_reminder_history(
 id bigint generated always as identity primary key,
 reminder_id uuid not null references studio_client_reminders(id) on delete cascade,
 recorded_at_utc timestamptz not null default now(),actor text not null,snapshot jsonb not null);
create or replace function studio_client_reminder_audit() returns trigger language plpgsql as $$
 begin
 insert into studio_client_reminder_history(reminder_id,actor,snapshot) values(new.id,coalesce(nullif(current_setting('studio.reminder_actor',true),''),new.created_by),to_jsonb(new));
 return new;
 end $$;
drop trigger if exists client_reminder_audit on studio_client_reminders;
create trigger client_reminder_audit after insert or update on studio_client_reminders for each row execute function studio_client_reminder_audit();

