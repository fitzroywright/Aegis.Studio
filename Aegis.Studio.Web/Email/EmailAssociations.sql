-- Additive migration: existing correspondence, thread IDs and attachments remain intact.
create or replace function studio_normalize_email(value text) returns text language sql immutable as $$
 select lower(btrim(case when value ~ '<[^<>]+>' then substring(value from '<([^<>]+)>') else coalesce(value,'') end))
$$;
alter table studio_conversations add column if not exists client_id uuid null references studio_clients(id);
alter table studio_conversations add column if not exists client_association_locked boolean not null default false;
create table if not exists studio_email_public_references(
    reference text primary key, conversation_id uuid null references studio_conversations(id),
    thread_id uuid null references studio_message_threads(id),
    check ((conversation_id is null) <> (thread_id is null)));
-- Full UUID references are collision-free within each namespace. Legacy 8-digit aliases are
-- resolved by candidate count, never by choosing the first matching row.
insert into studio_email_public_references(reference,conversation_id)
select 'AEGIS-'||upper(replace(id::text,'-','')),id from studio_conversations on conflict do nothing;
insert into studio_email_public_references(reference,thread_id)
select 'AEGIS-T'||upper(replace(id::text,'-','')),id from studio_message_threads on conflict do nothing;
create table if not exists studio_email_contacts(
    conversation_id uuid not null references studio_conversations(id),
    address text not null, added_by text not null, added_at_utc timestamptz not null default now(),
    primary key(conversation_id,address));
create table if not exists studio_email_association_history(
    id bigint generated always as identity primary key, conversation_id uuid null,
    external_message_id text null, action text not null, actor text not null,
    previous_value jsonb not null, new_value jsonb not null, created_at_utc timestamptz not null default now());
create table if not exists studio_email_intake_receipts(
    external_message_id text primary key, provider_message_id text null,
    conversation_id uuid null references studio_conversations(id), thread_id uuid null references studio_message_threads(id),
    payload_hash text null, message_id uuid null, internet_headers jsonb not null default '{}',
    staff_corrected boolean not null default false, attachments_complete boolean not null default false,
    received_at_utc timestamptz not null, check ((conversation_id is null) <> (thread_id is null)));
alter table studio_unrouted_email add column if not exists candidate_conversations uuid[] not null default '{}';
alter table studio_unrouted_email add column if not exists candidate_threads uuid[] not null default '{}';
alter table studio_unrouted_email add column if not exists routing_workspace_id uuid null;
alter table studio_outbound_email add column if not exists rfc_message_id_verified boolean not null default false;
create table if not exists studio_outbound_message_ids(
    delivery_id uuid not null references studio_outbound_email(id) on delete cascade,
    internet_message_id text not null, verified_at_utc timestamptz not null default now(),
    primary key(delivery_id,internet_message_id));
create index if not exists ix_outbound_actual_internet_id on studio_outbound_message_ids(internet_message_id);
create table if not exists studio_email_attachment_links(
    message_id uuid not null, attachment_id uuid not null references studio_attachments(id) on delete cascade,
    primary key(message_id,attachment_id));

alter table studio_email_intake_receipts add column if not exists payload_hash text null;

alter table studio_unrouted_email add column if not exists source_provider_message_id text null;

create unique index if not exists ux_studio_email_provider_receipt on studio_email_intake_receipts(provider_message_id) where provider_message_id is not null;


-- Shared unread state for canonical incoming inquiry/job email messages.
create table if not exists studio_shared_message_reads(
    message_id uuid primary key,
    reader_user_name text not null,
    read_at_utc timestamptz not null default now()
);
