create table if not exists studio_worker_types
(
    code text primary key,
    display_name text not null,
    can_be_primary boolean not null default true,
    is_active boolean not null default true,
    created_at_utc timestamptz not null default now(),
    updated_at_utc timestamptz not null default now()
);

create table if not exists studio_workers
(
    user_name text primary key,
    display_name text not null,
    worker_type_code text not null references studio_worker_types(code),
    email text null,
    phone text null,
    is_active boolean not null default true,
    created_at_utc timestamptz not null default now(),
    updated_at_utc timestamptz not null default now()
);

create table if not exists studio_job_worker_assignments
(
    id uuid primary key,
    job_id uuid not null references studio_jobs(id) on delete cascade,
    worker_user_name text not null references studio_workers(user_name),
    worker_type_code text not null references studio_worker_types(code),
    responsibility text not null,
    is_primary boolean not null default false,
    status text not null default 'Assigned',
    starts_at_utc timestamptz null,
    ends_at_utc timestamptz null,
    accepted_at_utc timestamptz null,
    completed_at_utc timestamptz null,
    created_at_utc timestamptz not null default now(),
    updated_at_utc timestamptz not null default now(),
    check (ends_at_utc is null or starts_at_utc is null or ends_at_utc > starts_at_utc)
);

create unique index if not exists ux_job_primary_worker
    on studio_job_worker_assignments(job_id) where is_primary;

create index if not exists ix_job_worker_assignments_worker_time
    on studio_job_worker_assignments(worker_user_name, starts_at_utc, ends_at_utc);

insert into studio_worker_types(code,display_name,can_be_primary)
values
    ('PHOTOGRAPHER','Photographer',true),
    ('EDITOR','Editor',false),
    ('RETOUCHER','Retoucher',false),
    ('REVIEWER','Reviewer',false),
    ('DELIVERY','Delivery',false)
on conflict(code) do update set display_name=excluded.display_name,can_be_primary=excluded.can_be_primary,updated_at_utc=now();

insert into studio_workers(user_name,display_name,worker_type_code,is_active,created_at_utc,updated_at_utc)
select p.user_name,p.display_name,'PHOTOGRAPHER',p.is_active,p.created_at_utc,p.updated_at_utc
from studio_photographers p
on conflict(user_name) do update set display_name=excluded.display_name,is_active=excluded.is_active,updated_at_utc=now();
