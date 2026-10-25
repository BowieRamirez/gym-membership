-- Phase 10 (D12, 2026-10-23): a permanent member QR code for identification, never payment.
-- The QR holds only a random code. The code never changes (renewal, expiry, pending payments, archive and restore keep it).
-- Only staff with check_ins:record (front desk and admins) can look a code up, through scan_member, which reads live data
-- and logs every scan. A scan never checks anyone in: the app calls the existing record_check_in for that.

-- A locked table (like user_last_seen): a column on profiles would be readable by everyone who can read the profile.
create table public.member_codes (
  user_id    uuid        primary key references public.members (user_id) on delete cascade,   -- a code is not history: a role switch that drops the members row (0011) drops it too
  code       text        not null unique default ('GYM1-' || replace(gen_random_uuid()::text, '-', '')),
  created_at timestamptz not null default now()
);
alter table public.member_codes enable row level security;
revoke all on public.member_codes from public, anon, authenticated;
grant select on public.member_codes to authenticated;
create policy member_codes_own on public.member_codes for select to authenticated using (user_id = auth.uid());

insert into public.member_codes (user_id) select user_id from public.members on conflict do nothing;

create function public.give_member_code() returns trigger
language plpgsql security definer set search_path='' as $$
begin
  insert into public.member_codes (user_id) values (new.user_id) on conflict do nothing;
  return new;
end $$;
revoke all on function public.give_member_code() from public, anon, authenticated;
create trigger members_give_code after insert on public.members for each row execute function public.give_member_code();

-- Every scan, kept forever. member_id is null when the code was not recognised.
create table public.member_scans (
  scan_id    bigint      generated always as identity primary key,
  scanned_at timestamptz not null default now(),
  scanned_by uuid        not null references public.profiles (id),
  member_id  uuid        references public.members (user_id),
  code_seen  text        not null,
  result     text        not null check (result in ('active', 'pending', 'expired', 'inactive', 'not_found'))
);
create index member_scans_member_idx on public.member_scans (member_id, scanned_at);
alter table public.member_scans enable row level security;
revoke all on public.member_scans from public, anon, authenticated;
grant select on public.member_scans to authenticated;
create policy member_scans_audit on public.member_scans for select to authenticated using (public.is_active_user() and public.has_permission('audit:read'));

-- Status, in this order: archived/deleted -> inactive; a running term -> active; a membership payment waiting -> pending;
-- otherwise expired (no membership). expires_at: the end of the unbroken run of terms for active, the last term's end for expired.
create function public.scan_member(p_code text) returns jsonb
language plpgsql security definer set search_path='' as $$
declare
  v_code text := left(trim(coalesce(p_code, '')), 100);
  v_member uuid; v_name text; v_active boolean; v_status text; v_expires timestamptz;
begin
  perform public.require_permission('check_ins:record');
  select c.user_id, coalesce(nullif(trim(p.full_name), ''), p.username), p.is_active into v_member, v_name, v_active
  from public.member_codes c join public.profiles p on p.id = c.user_id where c.code = v_code;

  if v_member is null then
    v_status := 'not_found';
  elsif not v_active then
    v_status := 'inactive';
  elsif public.is_active_member(v_member) then
    v_status := 'active';
    with recursive t as (
      select starts_at, ends_at from public.membership_terms where member_id = v_member and cancelled_at is null
    ), run(e) as (
      select ends_at from t where starts_at <= now() and ends_at > now()
      union
      select t.ends_at from run join t on t.starts_at <= run.e and t.ends_at > run.e
    ) select max(e) into v_expires from run;
  elsif exists (select 1 from public.payments where member_id = v_member and status = 'pending' and kind in ('membership', 'renewal')) then
    v_status := 'pending';
  else
    v_status := 'expired';
    select max(ends_at) into v_expires from public.membership_terms where member_id = v_member and cancelled_at is null and ends_at <= now();
  end if;

  insert into public.member_scans (scanned_by, member_id, code_seen, result) values (auth.uid(), v_member, v_code, v_status);
  return jsonb_build_object('result', v_status, 'member', v_member, 'name', v_name, 'expires', v_expires);
end $$;
revoke all on function public.scan_member(text) from public, anon;
grant execute on function public.scan_member(text) to authenticated;
