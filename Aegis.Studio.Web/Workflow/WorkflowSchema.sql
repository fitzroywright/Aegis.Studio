-- Additive, repeat-safe schema. Prior data is never deleted by this migration.
select pg_advisory_xact_lock(hashtextextended('studio-connected-workflow-schema',0));
create table if not exists studio_workflow_migrations(version integer primary key,applied_at_utc timestamptz not null default now());
alter table studio_workspaces add column if not exists system_key text;
create unique index if not exists ux_workspace_system_key on studio_workspaces(system_key) where system_key is not null;
insert into studio_workspaces(id,name,is_enabled,sort_order,created_at_utc,updated_at_utc,system_key)
values('7fe72808-a713-4d99-8b39-481c86061008','Unassigned',true,-1,now(),now(),'unassigned') on conflict(id) do nothing;
create or replace function studio_protect_unassigned() returns trigger language plpgsql as $$
begin
 if old.system_key='unassigned' and (tg_op='DELETE' or new.system_key is distinct from old.system_key or new.id<>old.id or new.name<>'Unassigned' or not new.is_enabled) then
  raise exception 'The permanent Unassigned Workspace cannot be removed, renamed or disabled';
 end if;
 if tg_op='DELETE' then return old; end if; return new;
end $$;
drop trigger if exists protect_unassigned on studio_workspaces;
create trigger protect_unassigned before update or delete on studio_workspaces for each row execute function studio_protect_unassigned();
alter table studio_jobs add column if not exists workflow_revision bigint not null default 0;
alter table studio_jobs add column if not exists stage_entered_at_utc timestamptz;
alter table studio_jobs add column if not exists workflow_closed_at_utc timestamptz;
alter table studio_jobs add column if not exists monitoring_started_at_utc timestamptz not null default now();
alter table studio_jobs add column if not exists blocked_reason text;
alter table studio_jobs add column if not exists correction_kind text;
alter table studio_jobs add column if not exists frontdesk_claim text;
alter table studio_jobs add column if not exists legacy_workflow_status text;
create table if not exists studio_workflow_history(id uuid primary key,job_id uuid not null references studio_jobs(id),previous_stage text,new_stage text not null,entered_at_utc timestamptz,exited_at_utc timestamptz,actor text not null,reason text,revision bigint not null,unique(job_id,revision));
create index if not exists ix_workflow_history_job on studio_workflow_history(job_id,revision);
create table if not exists studio_workflow_reconciliation(job_id uuid primary key references studio_jobs(id),legacy_status text not null,reason text not null,created_at_utc timestamptz not null default now());
create table if not exists studio_workflow_sessions(id uuid primary key,job_id uuid not null references studio_jobs(id),title text not null,shoot_id uuid unique references studio_shoot_sessions(id),required boolean not null default true,waiver_reason text,crew_finalized boolean not null default false,created_at_utc timestamptz not null default now());
create table if not exists studio_workflow_crew(session_id uuid not null references studio_workflow_sessions(id),user_name text not null,role text not null check(role in ('Lead','Additional')),assigned_by text not null,assigned_at_utc timestamptz not null default now(),notes text,attendance_start_utc timestamptz,attendance_end_utc timestamptz,ready boolean not null default false,waiver_reason text,primary key(session_id,user_name));
create unique index if not exists ux_session_one_lead on studio_workflow_crew(session_id) where role='Lead';
create table if not exists studio_workflow_commands(operation_id uuid primary key,job_id uuid not null references studio_jobs(id),fingerprint text not null,result_revision bigint not null);
create table if not exists studio_workflow_submissions(id uuid primary key,job_id uuid not null references studio_jobs(id),kind text not null,revision integer not null,manifest jsonb not null,submitted_by text not null,submitted_at_utc timestamptz not null default now(),status text not null default 'Pending',reviewed_by text,reviewed_at_utc timestamptz,note text,unique(job_id,kind,revision));
create table if not exists studio_workflow_overrides(id uuid primary key,job_id uuid not null references studio_jobs(id),kind text not null,actor text not null,reason text not null,original_requirement numeric,created_at_utc timestamptz not null default now());
create table if not exists studio_workflow_publications(id uuid primary key,job_id uuid not null references studio_jobs(id),submission_id uuid not null references studio_workflow_submissions(id),reference text not null,provider text not null,actor text not null,created_at_utc timestamptz not null default now());
create table if not exists studio_workflow_deliveries(id uuid primary key,job_id uuid not null references studio_jobs(id),method text not null,reference text,actor text not null,created_at_utc timestamptz not null default now());
create table if not exists studio_workflow_assignment_requests(id uuid primary key,job_id uuid not null references studio_jobs(id),task_id uuid references studio_job_activities(id),user_name text not null,kind text not null,note text,status text not null default 'Pending',decision_by text,decision_reason text,redirect_to text,created_at_utc timestamptz not null default now());
create unique index if not exists ux_assignment_request_pending on studio_workflow_assignment_requests(job_id,user_name,kind,coalesce(task_id,'00000000-0000-0000-0000-000000000000'::uuid)) where status='Pending';
create table if not exists studio_workflow_thresholds(stage text primary key,warning_hours double precision not null check(warning_hours>0),overdue_hours double precision not null,check(overdue_hours>warning_hours));
insert into studio_workflow_thresholds values('Pending',2,8),('Needs Assignment',4,24),('Needs Photographer Assignment',4,24),('Awaiting Scheduling',8,48),('Awaiting Upload',8,24),('Awaiting Production Review',4,24),('Active Production',24,72),('Awaiting Approval',24,72),('Changes Requested',8,24),('Ready for Delivery',24,72),('Delivered',24,72) on conflict do nothing;
-- Use the existing notification queue and worker; transactionally enqueue recipients below.
alter table studio_notifications add column if not exists workflow_event_key text;
create unique index if not exists ux_notification_workflow_event on studio_notifications(workflow_event_key,lower(user_name),channel) where workflow_event_key is not null;
create table if not exists studio_workflow_delivery_failures(event_key text primary key,job_id uuid not null references studio_jobs(id),audience text not null,subject text not null,body text not null,last_error text not null,created_at_utc timestamptz not null default now());
create table if not exists studio_workflow_audiences(audience text not null,user_name text not null,primary key(audience,user_name));
create or replace function studio_workflow_notify(p_job uuid,p_event text,p_audience text,p_subject text,p_body text) returns void language plpgsql as $$
declare p record; found_user boolean:=false;
begin
 for p in
  select distinct u.* from studio_user_profiles u join (
    select a.user_name from studio_workflow_audiences a where a.audience=p_audience
    union select j.job_lead from studio_jobs j where j.id=p_job and p_audience='Owner' and j.job_lead is not null
    union select wm.user_name from studio_workspace_members wm join studio_jobs j on j.workspace_id=wm.workspace_id where j.id=p_job and wm.is_active and (wm.manage_members or wm.admin_workspace) and p_audience='Manager'
    union select m.user_name from studio_global_operational_role_members m join studio_global_operational_roles r on r.id=m.role_id where m.is_active and r.is_active and r.name=p_audience
    union select c.user_name from studio_workflow_crew c join studio_workflow_sessions s on s.id=c.session_id where s.job_id=p_job and p_audience='Crew'
    union select p_audience where p_audience not in ('Owner','Manager','Crew','Front Desk','Production')
  ) targets on lower(targets.user_name)=lower(u.user_name)
  where not exists(select 1 from studio_employees e where lower(e.user_name)=lower(u.user_name) and (not e.is_active or not e.is_assignable))
 loop
  found_user:=true;
  if p.in_app_enabled then
   insert into studio_notifications(id,user_name,channel,event_type,subject,body,reference_type,reference_id,status,destination,attempts,is_read,created_at_utc,delivered_at_utc,workflow_event_key)
   values(gen_random_uuid(),p.user_name,'InApp','JobStatusChanged',p_subject,p_body,'Job',p_job::text,'Delivered',null,0,false,now(),now(),p_event) on conflict do nothing;
  end if;
  if p.email_enabled and p.email is not null then
   insert into studio_notifications(id,user_name,channel,event_type,subject,body,reference_type,reference_id,status,destination,attempts,is_read,created_at_utc,workflow_event_key)
   values(gen_random_uuid(),p.user_name,'Email','JobStatusChanged',p_subject,p_body,'Job',p_job::text,'Pending',p.email,0,false,now(),p_event) on conflict do nothing;
  end if;
 end loop;
 if not found_user then insert into studio_workflow_delivery_failures(event_key,job_id,audience,subject,body,last_error)
  values(p_event,p_job,p_audience,p_subject,p_body,'No active recipient profile configured') on conflict do nothing;
 else delete from studio_workflow_delivery_failures where event_key=p_event; end if;
end $$;
-- Map historical statuses only once. Unknown dates remain NULL, never backdated from last edit.
do $$ begin
 if not exists(select 1 from studio_workflow_migrations where version=1) then
  update studio_jobs set legacy_workflow_status=status,status=case status
   when 'New' then 'Pending' when 'Inquiry' then 'Pending' when 'Confirmed' then 'Needs Assignment'
   when 'Needs Scheduling' then case when job_lead is null then 'Needs Assignment' else 'Awaiting Scheduling' end
   when 'InProgress' then 'Photography In Progress' when 'Needs Review' then 'Awaiting Production Review'
   when 'In Production' then 'Active Production' when 'Ready for Client' then 'Ready for Delivery'
   when 'Published' then 'Ready for Delivery' when 'Closed' then 'Completed' else status end;
  insert into studio_workflow_reconciliation(job_id,legacy_status,reason)
   select id,legacy_workflow_status,'Legacy state/approval must be checked against recorded evidence' from studio_jobs
   where status not in ('Pending','Needs Assignment','Needs Photographer Assignment','Awaiting Scheduling','Scheduled','Photography In Progress','Awaiting Upload','Awaiting Production Review','Active Production','Awaiting Approval','Changes Requested','Ready for Delivery','Delivered','Completed','Cancelled') or legacy_workflow_status in ('Published','Changes Requested','Ready for Client','Confirmed') on conflict do nothing;
  insert into studio_workflow_sessions(id,job_id,title,shoot_id,required,crew_finalized)
   select id,job_id,'Photography',id,status<>'Cancelled',true from studio_shoot_sessions on conflict do nothing;
  insert into studio_workflow_crew(session_id,user_name,role,assigned_by)
   select id,lower(photographer_user_name),'Lead','Migration' from studio_shoot_sessions on conflict do nothing;
  insert into studio_workflow_crew(session_id,user_name,role,assigned_by)
   select id,lower(assistant_user_name),'Additional','Migration' from studio_shoot_sessions where nullif(trim(assistant_user_name),'') is not null and lower(assistant_user_name)<>lower(photographer_user_name) on conflict do nothing;
  insert into studio_workflow_migrations(version) values(1);
 end if;
end $$;
create or replace function studio_workflow_job_before() returns trigger language plpgsql as $$
begin
 if tg_op='INSERT' then new.stage_entered_at_utc:=now(); return new; end if;
 if new is distinct from old then new.workflow_revision:=old.workflow_revision+1; end if;
 if new.status is distinct from old.status then
  new.stage_entered_at_utc:=now();
  if new.status in ('Completed','Cancelled') then new.workflow_closed_at_utc:=now(); else new.workflow_closed_at_utc:=null; end if;
 end if;
 return new;
end $$;
create or replace function studio_workflow_job_after() returns trigger language plpgsql as $$
declare event_key text; actor text; reason text;
begin
 actor:=coalesce(nullif(current_setting('studio.actor',true),''),'System');reason:=nullif(current_setting('studio.reason',true),'');
 if tg_op='INSERT' or new.status is distinct from old.status then
  insert into studio_workflow_history(id,job_id,previous_stage,new_stage,entered_at_utc,actor,reason,revision)
  values(gen_random_uuid(),new.id,case when tg_op='UPDATE' then old.status else null end,new.status,new.stage_entered_at_utc,actor,reason,new.workflow_revision) on conflict do nothing;
  if tg_op='UPDATE' then update studio_workflow_history set exited_at_utc=now() where job_id=new.id and exited_at_utc is null and revision<>new.workflow_revision; end if;
  event_key:=new.id::text||':'||new.workflow_revision::text||':stage';
  perform studio_workflow_notify(new.id,event_key,case when new.status='Pending' then 'Front Desk' when new.status='Needs Assignment' then 'Manager' when new.status in ('Awaiting Production Review','Active Production','Awaiting Approval') then 'Production' else 'Owner' end,'Job: '||new.title,new.status);
 end if;
 return new;
end $$;
drop trigger if exists workflow_job_before on studio_jobs;
create trigger workflow_job_before before insert or update on studio_jobs for each row execute function studio_workflow_job_before();
drop trigger if exists workflow_job_after on studio_jobs;
create trigger workflow_job_after after insert or update on studio_jobs for each row execute function studio_workflow_job_after();

-- History/extra schedule staff are reconciled additively, including installations already at v1.
do $$ begin
 if not exists(select 1 from studio_workflow_migrations where version=2) then
  insert into studio_workflow_history(id,job_id,new_stage,entered_at_utc,actor,reason,revision)
  select gen_random_uuid(),id,status,stage_entered_at_utc,'Migration','Historical stage entry timestamp is unknown',workflow_revision from studio_jobs
  where not exists(select 1 from studio_workflow_history h where h.job_id=studio_jobs.id) on conflict do nothing;
  insert into studio_workflow_crew(session_id,user_name,role,assigned_by)
  select w.id,lower(p.user_name),'Additional','Migration' from studio_workflow_sessions w join studio_shoot_sessions s on s.id=w.shoot_id
  join studio_schedule_entry_resources er on er.entry_id=s.schedule_entry_id join studio_photographers p on p.schedule_resource_id=er.resource_id
  where not exists(select 1 from studio_workflow_crew c where c.session_id=w.id and lower(c.user_name)=lower(p.user_name)) on conflict do nothing;
  insert into studio_workflow_migrations(version) values(2);
 end if;
end $$;

create table if not exists studio_workflow_configuration_audit(id uuid primary key,action text not null,actor text not null,detail jsonb not null,created_at_utc timestamptz not null default now());
alter table studio_schedule_entry_resources add column if not exists attendance_start_utc timestamptz;
alter table studio_schedule_entry_resources add column if not exists attendance_end_utc timestamptz;
alter table studio_workflow_submissions add column if not exists contribution_evidence jsonb not null default '[]';
-- Any completed upload invalidates the declared contribution readiness, and any review/QC mutation changes the Job revision.
create or replace function studio_workflow_media_change() returns trigger language plpgsql as $$
begin
 update studio_jobs set workflow_revision=workflow_revision+1 where id=new.job_id;
 update studio_workflow_crew set ready=false where waiver_reason is null and session_id in(select id from studio_workflow_sessions where job_id=new.job_id);
 return new;
end $$;
drop trigger if exists workflow_media_change on studio_asset_versions;
create trigger workflow_media_change after insert on studio_asset_versions for each row execute function studio_workflow_media_change();
create or replace function studio_workflow_activity_change() returns trigger language plpgsql as $$
begin
 update studio_jobs set workflow_revision=workflow_revision+1 where id=new.job_id;
 if new.assigned_to is not null and (tg_op='INSERT' or new.assigned_to is distinct from old.assigned_to) then
  perform studio_workflow_notify(new.job_id,new.id::text||':'||new.updated_at_utc::text||':assignment',new.assigned_to,'Task assigned: '||new.title,new.title);
 end if;
 return new;
end $$;
drop trigger if exists workflow_activity_change on studio_job_activities;
create trigger workflow_activity_change after insert or update on studio_job_activities for each row execute function studio_workflow_activity_change();
-- WorkDay retains the authoritative shoot record; compatibility writes keep the session model in step.
create or replace function studio_workflow_shoot_change() returns trigger language plpgsql as $$
declare session uuid;
begin
 if current_setting('studio.connected_command',true)='true' then return new; end if;
 select id into session from studio_workflow_sessions where shoot_id=new.id;
 if session is null then
  select id into session from studio_workflow_sessions where job_id=new.job_id and shoot_id is null order by created_at_utc limit 1;
  if session is null then session:=new.id;insert into studio_workflow_sessions(id,job_id,title,shoot_id,crew_finalized) values(session,new.job_id,'Photography',new.id,true);
  else update studio_workflow_sessions set shoot_id=new.id,crew_finalized=true where id=session;end if;
 end if;
 if tg_op='INSERT' or new.photographer_user_name is distinct from old.photographer_user_name or new.assistant_user_name is distinct from old.assistant_user_name then
  delete from studio_workflow_crew where session_id=session;
  insert into studio_workflow_crew(session_id,user_name,role,assigned_by) values(session,lower(new.photographer_user_name),'Lead',coalesce(nullif(current_setting('studio.actor',true),''),'WorkDay'));
  if nullif(trim(new.assistant_user_name),'') is not null and lower(new.assistant_user_name)<>lower(new.photographer_user_name) then
   insert into studio_workflow_crew(session_id,user_name,role,assigned_by) values(session,lower(new.assistant_user_name),'Additional','WorkDay');
  end if;
  insert into studio_workflow_crew(session_id,user_name,role,assigned_by)
   select session,lower(p.user_name),'Additional','WorkDay' from studio_schedule_entry_resources er join studio_photographers p on p.schedule_resource_id=er.resource_id where er.entry_id=new.schedule_entry_id on conflict do nothing;
 end if;
 update studio_jobs set workflow_revision=workflow_revision+1 where id=new.job_id;
 return new;
end $$;
drop trigger if exists workflow_shoot_change on studio_shoot_sessions;
create trigger workflow_shoot_change after insert or update on studio_shoot_sessions for each row execute function studio_workflow_shoot_change();

-- Transfer requests preserve the original workspace until its destination manager approves.
create table if not exists studio_workflow_transfer_requests(
 id uuid primary key,
 job_id uuid not null references studio_jobs(id),
 source_workspace_id uuid not null references studio_workspaces(id),
 destination_workspace_id uuid not null references studio_workspaces(id),
 requested_by text not null,
 reason text not null,
 status text not null default 'Pending' check(status in ('Pending','Approved','Rejected','Superseded')),
 decided_by text,
 decision_reason text,
 created_at_utc timestamptz not null default now(),
 decided_at_utc timestamptz
);
create unique index if not exists ux_workflow_pending_transfer on studio_workflow_transfer_requests(job_id) where status='Pending';

-- Independent delivery obligations survive the workflow's overall Delivered stage.
create table if not exists studio_workflow_delivery_requirements(
 id uuid primary key,
 job_id uuid not null references studio_jobs(id),
 title text not null,
 required boolean not null default true,
 created_by text not null,
 created_at_utc timestamptz not null default now(),
 delivered_at_utc timestamptz,
 delivery_method text,
 delivery_reference text,
 delivered_by text,
 waived boolean not null default false,
 waiver_reason text,
 waived_by text,
 constraint delivery_proof_or_waiver check(not (waived and delivered_at_utc is not null))
);
create index if not exists ix_studio_workflow_delivery_requirements_job on studio_workflow_delivery_requirements(job_id);

-- Upload proof is a separate immutable receipt, not a photographer's assertion of readiness.
create table if not exists studio_workflow_upload_batches(
 id uuid primary key,
 job_id uuid not null references studio_jobs(id),
 session_id uuid not null references studio_workflow_sessions(id),
 expected_files integer not null check(expected_files>0),
 completed_files integer not null check(completed_files>=0),
 failed_files integer not null check(failed_files>=0),
 backup_confirmed boolean not null default false,
 recorded_by text not null,
 recorded_at_utc timestamptz not null default now(),
 constraint upload_batch_counts_valid check(completed_files+failed_files<=expected_files)
);
create index if not exists ix_studio_workflow_upload_batches_session on studio_workflow_upload_batches(session_id,recorded_at_utc desc);
