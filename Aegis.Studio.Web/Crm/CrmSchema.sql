create or replace function studio_crm_normalize_phone(value text) returns text language sql immutable parallel safe as $$
 select case when length(digits)=10 and left(digits,3) in ('876','658') then '1'||digits else digits end from (select regexp_replace(coalesce(value,''),'[^0-9]','','g') digits) normalized
$$;
-- CRM batch
-- Additive; existing client IDs, job snapshots and financial ledgers are retained.
alter table studio_conversations add column if not exists client_id uuid references studio_clients(id);
create table if not exists studio_crm_migrations(version integer primary key,applied_at_utc timestamptz not null default now());
alter table studio_clients add column if not exists client_type text not null default 'Individual';
alter table studio_clients add column if not exists legal_name text;
alter table studio_clients add column if not exists first_name text;
alter table studio_clients add column if not exists last_name text;
alter table studio_clients add column if not exists organisation_name text;
alter table studio_clients add column if not exists primary_workspace_id uuid references studio_workspaces(id);
alter table studio_clients add column if not exists account_owner text;
alter table studio_clients add column if not exists relationship_status text not null default 'Active';
alter table studio_clients add column if not exists client_since date not null default current_date;
alter table studio_clients add column if not exists source text;
alter table studio_clients add column if not exists tags text[] not null default '{}';
alter table studio_clients add column if not exists metadata jsonb not null default '{}';
alter table studio_clients add column if not exists archived_at_utc timestamptz;
alter table studio_clients add column if not exists merged_into_id uuid references studio_clients(id);
create index if not exists ix_crm_client_scope on studio_clients(primary_workspace_id,relationship_status,client_type);
create index if not exists ix_crm_client_name on studio_clients(lower(display_name),id);
create index if not exists ix_crm_client_email on studio_clients(lower(btrim(email))) where email is not null;
create index if not exists ix_crm_client_phone on studio_clients((studio_crm_normalize_phone(phone))) where phone is not null;
create index if not exists ix_crm_client_tags on studio_clients using gin(tags);
create index if not exists ix_crm_conversation_client on studio_conversations(client_id,created_at_utc desc);
create table if not exists studio_crm_contacts(
 id uuid primary key,client_id uuid not null references studio_clients(id),person_id uuid not null references studio_clients(id),
 role text not null,is_primary_billing boolean not null default false,is_booking_contact boolean not null default false,
 created_at_utc timestamptz not null default now(),unique(client_id,person_id,role),check(client_id<>person_id));
create index if not exists ix_crm_contacts_person on studio_crm_contacts(person_id);
create table if not exists studio_crm_tasks(
 id uuid primary key,client_id uuid not null references studio_clients(id),workspace_id uuid not null references studio_workspaces(id),
 job_id uuid references studio_jobs(id),enquiry_id uuid references studio_conversations(id),assigned_to text not null,
 task_type text not null,priority text not null default 'Normal' check(priority in ('Low','Normal','High','Urgent')),
 status text not null default 'Open' check(status in ('Open','In Progress','Completed','Cancelled')),
 due_at_utc timestamptz not null,notes text,completed_at_utc timestamptz,created_by text not null,
 created_at_utc timestamptz not null default now(),updated_at_utc timestamptz not null default now(),automation_key text unique);
create index if not exists ix_crm_tasks_due on studio_crm_tasks(workspace_id,status,due_at_utc);
create index if not exists ix_crm_tasks_client on studio_crm_tasks(client_id,due_at_utc);
create index if not exists ix_crm_tasks_owner on studio_crm_tasks(lower(assigned_to),status,due_at_utc);
create table if not exists studio_crm_activity(
 id uuid primary key,client_id uuid not null references studio_clients(id),workspace_id uuid references studio_workspaces(id),
 event_type text not null,description text not null,actor text not null,related_id uuid,created_at_utc timestamptz not null default now());
create index if not exists ix_crm_activity_client on studio_crm_activity(client_id,created_at_utc desc,id);
create table if not exists studio_crm_views(
 id uuid primary key,user_name text not null,name text not null,definition jsonb not null,unique(user_name,name));
alter table studio_conversations add column if not exists lead_status text not null default 'New';
alter table studio_conversations add column if not exists crm_metadata jsonb not null default '{}';
create index if not exists ix_crm_leads on studio_conversations(workspace_id,lead_status) where job_id is null;
update studio_clients c set client_since=least(c.client_since,(select min(j.created_at_utc)::date from studio_jobs j where j.client_id=c.id))
 where not exists(select 1 from studio_crm_migrations where version=1) and exists(select 1 from studio_jobs j where j.client_id=c.id);
insert into studio_crm_migrations(version) values(1) on conflict do nothing;
-- Durable CRM events feed the existing Studio workflow runner (not a second engine).
create table if not exists studio_crm_events(
 id uuid primary key default gen_random_uuid(),client_id uuid not null references studio_clients(id),workspace_id uuid references studio_workspaces(id),
 event_type text not null,event_key text not null unique,payload jsonb not null default '{}',created_at_utc timestamptz not null default now(),processed_at_utc timestamptz);
create index if not exists ix_crm_events_pending on studio_crm_events(created_at_utc) where processed_at_utc is null;
create table if not exists studio_crm_automation_receipts(action_key text primary key,client_id uuid not null references studio_clients(id),created_at_utc timestamptz not null default now());
create or replace function studio_crm_capture_event() returns trigger language plpgsql as $$
declare value jsonb:=to_jsonb(new); old_value jsonb; client uuid; workspace uuid; event text; key text;
begin
 if tg_op='UPDATE' then old_value:=to_jsonb(old); end if;
 if tg_table_name='studio_clients' then
   client:=new.id;workspace:=new.primary_workspace_id;event:='ClientCreated';
 elsif tg_table_name='studio_conversations' then
   client:=new.client_id;workspace:=new.workspace_id;
   if tg_op='INSERT' or old_value->>'client_id' is distinct from value->>'client_id' then event:='EnquiryReceived';
   elsif old_value->>'lead_status' is distinct from value->>'lead_status' then event:='LeadStatusChanged'; end if;
 elsif tg_table_name='studio_client_quotes' then
   client:=new.client_id;workspace:=(new.payload->>'workspaceId')::uuid;
   if new.status='Sent' and old_value->>'status' is distinct from value->>'status' then event:='QuoteSent';end if;
 else
   if tg_table_name='studio_jobs' then
     client:=new.client_id;workspace:=new.workspace_id;
     if new.status='Completed' and old_value->>'status' is distinct from value->>'status' then event:='JobCompleted';
     elsif new.status in ('Confirmed','Needs Assignment') and old_value->>'status' is distinct from value->>'status' then event:='BookingConfirmed';end if;
   else
     select j.client_id,j.workspace_id into client,workspace from studio_jobs j where j.id=(value->>'job_id')::uuid;
     if tg_table_name='studio_shoot_sessions' and value->>'status'='Completed' and old_value->>'status' is distinct from value->>'status' then event:='SessionCompleted';
     elsif tg_table_name='studio_workflow_publications' then event:='GalleryDelivered';end if;
   end if;
 end if;
 if client is not null and event is not null then
   key:=tg_table_name||':'||(value->>'id')||':'||event||':'||coalesce(value->>'lead_status',value->>'status','')||':'||txid_current()::text;
   insert into studio_crm_events(client_id,workspace_id,event_type,event_key,payload) values(client,workspace,event,key,value) on conflict do nothing;
 end if;
 return new;
end $$;
do $$ begin
 if not exists(select 1 from pg_trigger where tgname='trg_crm_client_created' and tgrelid='studio_clients'::regclass) then
 create trigger trg_crm_client_created after insert on studio_clients for each row execute function studio_crm_capture_event();
 create trigger trg_crm_enquiry after insert or update of client_id,lead_status on studio_conversations for each row execute function studio_crm_capture_event();
 create trigger trg_crm_quote after update of status on studio_client_quotes for each row execute function studio_crm_capture_event();
 create trigger trg_crm_job after update of status on studio_jobs for each row execute function studio_crm_capture_event();
 create trigger trg_crm_session after update of status on studio_shoot_sessions for each row execute function studio_crm_capture_event();
 create trigger trg_crm_gallery after insert on studio_workflow_publications for each row execute function studio_crm_capture_event();
 end if;
end $$;
insert into studio_crm_migrations(version) values(2) on conflict do nothing;
alter table studio_workspaces add column if not exists crm_automation_enabled boolean not null default false;
-- Respect explicit CRM channel opt-outs in the existing outbox, including retries.
create or replace function studio_crm_channel_allowed(client uuid,channel text) returns boolean language sql stable as $$
 select coalesce((select
   not coalesce((c.metadata->'communicationPreferences'->>'optOut')::boolean,false)
   and coalesce((coalesce(c.metadata->'communicationPreferences'->>(lower(channel)||'Allowed'),case when lower(channel)='whatsapp' then c.metadata->'communicationPreferences'->>'whatsAppAllowed' end))::boolean,true)
   and coalesce((select (consent->>'granted')::boolean from jsonb_array_elements(coalesce(c.metadata->'consentRecords','[]')) with ordinality as x(consent,n)
        where lower(consent->>'channel')=lower(channel) order by n desc limit 1),true)
   from studio_clients c where c.id=client),true)
$$;
create or replace function studio_crm_guard_delivery() returns trigger language plpgsql as $$
declare client uuid;
begin
 if new.status in ('Pending','Retry','Sending') then
   select e.client_id into client from studio_conversations e where e.id=new.conversation_id;
   if client is not null and not studio_crm_channel_allowed(client,coalesce(new.delivery_channel,'Email')) then
     new.status:='Attention';new.last_error:='Client communication preference or consent blocks this channel.';
   end if;
 end if;
 return new;
end $$;
do $$ begin
 if not exists(select 1 from pg_trigger where tgname='trg_crm_delivery_consent' and tgrelid='studio_outbound_email'::regclass) then
 create trigger trg_crm_delivery_consent before insert or update of status on studio_outbound_email for each row execute function studio_crm_guard_delivery();
 end if;
end $$;
insert into studio_crm_migrations(version) values(3) on conflict do nothing;
-- CRM batch
-- Built-in PostgreSQL full text indexing, no privileged extension installation.
create or replace function studio_crm_search_document(name text,email text,phone text,organisation text,legal text,first_name text,last_name text,metadata jsonb)
returns tsvector language sql immutable parallel safe as $$
 select to_tsvector('simple',coalesce(name,'')||' '||coalesce(email,'')||' '||coalesce(phone,'')||' '||coalesce(regexp_replace(phone,'[^0-9]','','g'),'')||' '||coalesce(organisation,'')||' '||coalesce(legal,'')||' '||coalesce(first_name,'')||' '||coalesce(last_name,'')||' '||coalesce(metadata->>'alternateNames',''))
$$;
create or replace function studio_crm_search_query(value text) returns tsquery language sql immutable parallel safe as $$
 select to_tsquery('simple',string_agg(quote_literal(token)||':*',' & ')) from unnest(tsvector_to_array(to_tsvector('simple',value))) token
$$;
-- CRM batch
create index if not exists ix_crm_client_search on studio_clients using gin(studio_crm_search_document(display_name,email,phone,organisation_name,legal_name,first_name,last_name,metadata));
create index if not exists ix_crm_client_owner on studio_clients(lower(account_owner),relationship_status);
insert into studio_crm_migrations(version) values(4) on conflict do nothing;
