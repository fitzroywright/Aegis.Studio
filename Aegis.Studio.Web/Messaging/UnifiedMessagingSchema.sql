create table if not exists studio_address_routes(id uuid primary key,email_address text not null,payload jsonb not null);
create unique index if not exists ux_address_routes_email on studio_address_routes(lower(email_address)) where email_address<>'';
alter table studio_inapp_messages add column if not exists send_state text not null default 'Ready';
alter table studio_inapp_messages add column if not exists subject text not null default '';
alter table studio_inapp_messages add column if not exists recipient_addresses text[] not null default '{}';
alter table studio_inapp_messages add column if not exists internet_message_id text null;
alter table studio_inapp_messages add column if not exists internet_headers jsonb not null default '{}';
alter table studio_inapp_messages add column if not exists client_request_id uuid null;
alter table studio_inapp_messages add column if not exists request_hash text null;
create unique index if not exists ux_inapp_message_request on studio_inapp_messages(lower(sender_user_name),client_request_id) where client_request_id is not null;
alter table studio_outbound_email alter column conversation_id drop not null;
alter table studio_outbound_email add column if not exists studio_thread_id uuid null references studio_message_threads(id) on delete cascade;
alter table studio_outbound_email add column if not exists studio_message_id uuid null;
alter table studio_outbound_email add column if not exists rfc_message_id text null;
alter table studio_outbound_email add column if not exists sender_mailbox text null;
create unique index if not exists ux_outbound_studio_recipient on studio_outbound_email(studio_message_id,lower(to_address),delivery_channel) where studio_message_id is not null;
create index if not exists ix_outbound_rfc_message on studio_outbound_email(rfc_message_id);
create table if not exists studio_message_deliveries(
    message_id uuid not null,recipient text not null,transport text not null,status text not null,
    destination_id uuid null,detail text null,created_at_utc timestamptz not null default now(),
    primary key(message_id,recipient,transport));
create table if not exists studio_inbound_receipts(
    provider_identity text not null,external_message_id text not null,destination_key text not null,
    message_id uuid null,status text not null,created_at_utc timestamptz not null default now(),
    primary key(provider_identity,external_message_id,destination_key));
create table if not exists studio_message_route_recipients(
    message_id uuid not null references studio_inapp_messages(id) on delete cascade,
    destination_id uuid not null,primary key(message_id,destination_id));
alter table studio_inapp_message_attachments add column if not exists source_key text null;
create unique index if not exists ux_message_attachment_source on studio_inapp_message_attachments(source_key) where source_key is not null;

alter table studio_unrouted_email add column if not exists file_attachments jsonb not null default '[]';
