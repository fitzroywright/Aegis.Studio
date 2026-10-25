alter table studio_conversations add column if not exists intake_reference text;
create unique index if not exists ux_conversation_intake_reference on studio_conversations(intake_reference) where intake_reference is not null;
create table if not exists studio_intake_sequences(year integer,source text,value integer not null check(value between 1 and 999999),primary key(year,source));
create or replace function studio_assign_intake_reference() returns trigger language plpgsql as $$
declare y integer; n integer;
begin
 if new.intake_reference is null then
  y=extract(year from new.created_at_utc at time zone 'America/Jamaica');
  insert into studio_intake_sequences values(y,'S',1) on conflict(year,source) do update set value=studio_intake_sequences.value+1 returning value into n;
  new.intake_reference=y::text||'-S-'||lpad(n::text,6,'0');
 end if;
 return new;
end $$;
drop trigger if exists assign_intake_reference on studio_conversations;
create trigger assign_intake_reference before insert on studio_conversations for each row execute function studio_assign_intake_reference();
create table if not exists studio_website_imports(source text not null,external_id uuid not null,revision bigint not null,conversation_id uuid not null references studio_conversations(id),request_id uuid,payload jsonb not null,primary key(source,external_id));
create table if not exists studio_website_cursor(source text primary key,cursor text not null);
create table if not exists studio_intake_rosters(conversation_id uuid primary key references studio_conversations(id),revision bigint not null default 0,rows jsonb not null default '[]',updated_by text not null,updated_at_utc timestamptz not null default now());
create table if not exists studio_intake_attachments(id uuid primary key,conversation_id uuid not null references studio_conversations(id),metadata jsonb not null,local_attachment_id uuid,state text not null);
create table if not exists studio_external_payment_events(source text,event_id text,fingerprint text not null,job_id uuid not null references studio_jobs(id),payload jsonb not null,created_at_utc timestamptz not null default now(),primary key(source,event_id));
alter table studio_job_subjects add column if not exists pickup_notification text not null default 'None';
alter table studio_job_subjects add column if not exists preference_source text;
alter table studio_job_subjects add column if not exists preference_at_utc timestamptz;
alter table studio_client_reminders add column if not exists subject_id uuid references studio_job_subjects(id);
create or replace function studio_apply_intake_roster(cv uuid) returns void language plpgsql as $$
declare target uuid; row jsonb; sid uuid; matches integer;
begin
 select job_id into target from studio_conversations where id=cv;
 if target is null then return; end if;
 for row in select jsonb_array_elements(rows) from studio_intake_rosters where conversation_id=cv loop
  select count(*),min(id::text)::uuid into matches,sid from studio_job_subjects where job_id=target and not is_system and (lower(reference)=lower(btrim(row->>'reference')) or id::text=row->>'subjectId');
  if matches>1 then raise exception 'Ambiguous subject reference'; end if;
  sid=coalesce(sid,gen_random_uuid());
  insert into studio_job_subjects(id,job_id,name,reference,notes,collection_name,contact_name,contact_email,contact_phone,schedule_location,scheduled_at_utc,updated_by,pickup_notification,preference_source,preference_at_utc)
  values(sid,target,row->>'name',row->>'reference',coalesce(row->>'notes',''),coalesce(nullif(row->>'collection',''),'General'),coalesce(row->>'contactName',''),coalesce(row->>'contactEmail',''),coalesce(row->>'contactPhone',''),coalesce(row->>'scheduleLocation',''),nullif(row->>'scheduledAtUtc','')::timestamptz,'Intake',coalesce(row->>'pickupNotification','None'),'Intake',now())
  on conflict(id) do update set name=excluded.name,notes=case when row ? 'notes' then excluded.notes else studio_job_subjects.notes end,
   collection_name=case when row ? 'collection' then excluded.collection_name else studio_job_subjects.collection_name end,
   contact_name=case when row ? 'contactName' then excluded.contact_name else studio_job_subjects.contact_name end,
   contact_email=case when row ? 'contactEmail' then excluded.contact_email else studio_job_subjects.contact_email end,
   contact_phone=case when row ? 'contactPhone' then excluded.contact_phone else studio_job_subjects.contact_phone end,
   schedule_location=case when row ? 'scheduleLocation' then excluded.schedule_location else studio_job_subjects.schedule_location end,
   scheduled_at_utc=case when row ? 'scheduledAtUtc' then excluded.scheduled_at_utc else studio_job_subjects.scheduled_at_utc end,
   pickup_notification=case when row ? 'pickupNotification' then excluded.pickup_notification else studio_job_subjects.pickup_notification end,
   preference_source='Intake',preference_at_utc=now(),updated_by='Intake',updated_at_utc=now();
 end loop;
end $$;
create or replace function studio_promote_intake_roster() returns trigger language plpgsql as $$
begin if new.job_id is distinct from old.job_id and new.job_id is not null then perform studio_apply_intake_roster(new.id); end if;return new;end $$;
drop trigger if exists promote_intake_roster on studio_conversations;
create trigger promote_intake_roster after update of job_id on studio_conversations for each row execute function studio_promote_intake_roster();

create table if not exists studio_website_import_errors(source text,external_id uuid,revision bigint,error_type text,updated_at_utc timestamptz not null default now(),primary key(source,external_id));
create table if not exists studio_intake_roster_conflicts(conversation_id uuid primary key references studio_conversations(id),rows jsonb not null,revision bigint not null,created_at_utc timestamptz not null default now());

alter table studio_website_import_errors add column if not exists payload jsonb;

create table if not exists studio_intake_activity(conversation_id uuid primary key references studio_conversations(id),published boolean not null default false,created_at_utc timestamptz not null default now());

alter table studio_website_imports add column if not exists last_status text;

-- Dedicated gallery access inbox; kept separate from the booking and inquiry intake contract.
create table if not exists studio_website_gallery_access_requests(
 source text not null,
 external_id uuid not null,
 client_id uuid not null references studio_clients(id),
 gallery_id uuid null,
 conversation_id uuid not null references studio_conversations(id),
 payload jsonb not null,
 imported_at_utc timestamptz not null default now(),
 reviewed_by text,
 reviewed_at_utc timestamptz,
 primary key(source,external_id)
);
create index if not exists ix_website_gallery_access_client on studio_website_gallery_access_requests(client_id, imported_at_utc desc);

create table if not exists studio_gallery_exports(
 publication_id uuid primary key,
 state text not null,
 last_error text,
 updated_at_utc timestamptz not null default now(),
 conversation_id uuid null references studio_conversations(id),
 job_id uuid null,
 client_id uuid null references studio_clients(id)
);
alter table studio_gallery_exports add column if not exists conversation_id uuid null references studio_conversations(id);
alter table studio_gallery_exports add column if not exists job_id uuid null;
alter table studio_gallery_exports add column if not exists client_id uuid null references studio_clients(id);

-- Immutable client snapshots. A lost API acknowledgement replays the same source/submission.
create table if not exists studio_website_gallery_selections(
 source text not null,
 external_id uuid not null,
 publication_id uuid not null references studio_gallery_exports(publication_id),
 client_id uuid not null references studio_clients(id),
 conversation_id uuid not null references studio_conversations(id),
 payload jsonb not null,
 imported_at_utc timestamptz not null default now(),
 primary key(source,external_id)
);
