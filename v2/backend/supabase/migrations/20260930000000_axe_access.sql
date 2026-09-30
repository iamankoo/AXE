-- AXE v2 access backend.
--
-- Data policy (spec §13): payment/invitation requests are TEMPORARY. On approve/reject the
-- screenshot is deleted and the row's personal/payment fields are nulled; the stub that the
-- client polls for its result is removed after a short pickup window. Unanswered requests
-- expire. Only the minimum needed for an active authorization (id, plan, device hash,
-- expiry) is kept, and only until it expires.
--
-- Security: row-level security is enabled on every table with NO policies, so the anon and
-- authenticated roles can read or write nothing. Only the Edge Functions (service role)
-- touch these tables, after their own validation and admin checks.

create table public.access_requests (
  id               uuid primary key default gen_random_uuid(),
  kind             text        not null check (kind in ('payment', 'invite')),
  status           text        not null default 'pending' check (status in ('pending', 'approved', 'rejected')),
  plan             text        not null check (plan in ('1h', '5h', '10h')),
  expected_amount  integer     not null,
  -- personal / payment fields: nulled as soon as the admin decides
  name             text,
  amount_paid      numeric(10, 2),
  utr              text,
  invite_code      text,
  screenshot_path  text,
  duplicate_utr    boolean     not null default false,
  -- client binding
  device_hash      text        not null check (device_hash ~ '^[0-9a-f]{64}$'),
  poll_token_hash  text        not null,
  -- result
  grant_token      text,
  created_at       timestamptz not null default now(),
  decided_at       timestamptz,
  -- pending: review deadline; decided: pickup deadline for the client's result
  expires_at       timestamptz not null
);

create index access_requests_pending_idx on public.access_requests (created_at) where status = 'pending';
create index access_requests_device_idx on public.access_requests (device_hash) where status = 'pending';
create index access_requests_expiry_idx on public.access_requests (expires_at);

-- Active authorizations: the minimum needed to validate a grant and allow revocation.
create table public.access_grants (
  jti          uuid primary key,
  request_id   uuid        not null,
  plan         text        not null,
  device_hash  text        not null,
  issued_at    timestamptz not null,
  expires_at   timestamptz not null,
  revoked_at   timestamptz
);

create index access_grants_expiry_idx on public.access_grants (expires_at);

-- Keyed hashes (HMAC) of recently used UTR/reference numbers, to warn the admin about a
-- reused payment reference. No plaintext; expires after 7 days.
create table public.used_references (
  ref_hash  text primary key,
  used_at   timestamptz not null default now()
);

-- AXE Admin users (Supabase Auth users allowed to review requests).
create table public.admins (
  user_id     uuid primary key references auth.users (id) on delete cascade,
  created_at  timestamptz not null default now()
);

-- FCM registration tokens of signed-in AXE Admin devices.
create table public.admin_devices (
  token       text primary key,
  user_id     uuid        not null references auth.users (id) on delete cascade,
  updated_at  timestamptz not null default now()
);

-- Sliding-window rate limiting for public endpoints.
create table public.rate_events (
  key  text        not null,
  at   timestamptz not null default now()
);

create index rate_events_key_idx on public.rate_events (key, at);

alter table public.access_requests  enable row level security;
alter table public.access_grants    enable row level security;
alter table public.used_references  enable row level security;
alter table public.admins           enable row level security;
alter table public.admin_devices    enable row level security;
alter table public.rate_events      enable row level security;

revoke all on public.access_requests, public.access_grants, public.used_references,
              public.admins, public.admin_devices, public.rate_events
  from anon, authenticated;

-- Private bucket for payment screenshots: 5 MB, images only, never public.
insert into storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
values ('payment-screenshots', 'payment-screenshots', false, 5242880,
        array['image/png', 'image/jpeg', 'image/webp'])
on conflict (id) do nothing;

-- Atomic rate-limit check: records an event and returns how many fell in the window.
create or replace function public.axe_rate_hit(p_key text, p_window_seconds integer)
returns integer
language plpgsql
security definer
set search_path = public
as $$
declare
  hits integer;
begin
  delete from public.rate_events where key = p_key and at < now() - make_interval(secs => p_window_seconds);
  insert into public.rate_events (key) values (p_key);
  select count(*) into hits from public.rate_events where key = p_key;
  return hits;
end;
$$;

revoke all on function public.axe_rate_hit(text, integer) from public, anon, authenticated;

-- Expired rows (the Edge Functions also remove the matching screenshots from Storage).
create or replace function public.axe_cleanup_rows()
returns void
language sql
security definer
set search_path = public
as $$
  delete from public.access_grants   where expires_at < now() - interval '1 day';
  delete from public.used_references where used_at    < now() - interval '7 days';
  delete from public.rate_events     where at         < now() - interval '1 day';
$$;

revoke all on function public.axe_cleanup_rows() from public, anon, authenticated;
