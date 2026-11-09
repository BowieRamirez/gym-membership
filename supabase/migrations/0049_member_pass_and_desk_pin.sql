-- Phase 2 of the 2026-11-09 plan (owner decisions F6, F8).
-- F6 (reverses part of 0034 and D12): the member's QR is a one-use pass from the server that lasts 5 minutes. Scanning marks it
-- used, so a screenshot is useless, and the permanent member_codes stop working (kept for history). A QR check-in needs a pass that
-- this desk scanned in the last 10 minutes, so the desk can't claim a QR check-in it never scanned. The offline copy loses the codes.
-- F8: a check-in by name, or one queued offline, needs the member's 4-digit desk PIN (hashed). Five wrong tries lock it for
-- 15 minutes. Only Admin and System admin may skip the PIN, and only with a reason, which is kept on the check-in.

create extension if not exists pgcrypto with schema extensions;

create table public.member_passes (
  pass_id     bigint      generated always as identity primary key,
  member_id   uuid        not null references public.members (user_id) on delete cascade,
  code        text        not null unique default ('GYM2-' || replace(gen_random_uuid()::text, '-', '')),
  issued_at   timestamptz not null default now(),
  expires_at  timestamptz not null default now() + interval '5 minutes',
  scanned_at  timestamptz,
  scanned_by  uuid        references public.profiles (id),
  check_in_id bigint      references public.gym_check_ins (check_in_id)
);
create index member_passes_member_idx on public.member_passes (member_id, scanned_at);
alter table public.member_passes enable row level security;
revoke all on public.member_passes from public, anon, authenticated;

create table public.desk_pins (
  user_id      uuid        primary key references public.members (user_id) on delete cascade,
  pin_hash     text        not null,
  fails        int         not null default 0,
  locked_until timestamptz,
  updated_at   timestamptz not null default now()
);
alter table public.desk_pins enable row level security;
revoke all on public.desk_pins from public, anon, authenticated;

alter table public.gym_check_ins add column reason text;   -- set when an Admin or System admin checked someone in without their PIN
alter table public.member_scans drop constraint member_scans_result_check,
  add constraint member_scans_result_check check (result in ('active', 'pending', 'expired', 'inactive', 'not_found', 'old_code'));

-- The signed-in member's new pass. An unused earlier pass stops working.
create function public.issue_member_pass() returns table(code text, expires_at timestamptz)
language plpgsql security definer set search_path='' as $$
begin
  perform public.require_active_user();
  if not exists (select 1 from public.members where user_id = auth.uid()) then perform public.fail('forbidden', '42501'); end if;
  update public.member_passes p set expires_at = now() where p.member_id = auth.uid() and p.scanned_at is null and p.expires_at > now();
  return query insert into public.member_passes (member_id) values (auth.uid()) returning member_passes.code, member_passes.expires_at;
end $$;
revoke all on function public.issue_member_pass() from public, anon, authenticated;
grant execute on function public.issue_member_pass() to authenticated;

create function public.set_desk_pin(p_pin text) returns void
language plpgsql security definer set search_path='' as $$
begin
  perform public.require_active_user();
  if not exists (select 1 from public.members where user_id = auth.uid()) then perform public.fail('forbidden', '42501'); end if;
  if p_pin is null or p_pin !~ '^[0-9]{4}$' then perform public.fail('bad_pin', '22023'); end if;
  insert into public.desk_pins (user_id, pin_hash) values (auth.uid(), extensions.crypt(p_pin, extensions.gen_salt('bf')))
  on conflict (user_id) do update set pin_hash = excluded.pin_hash, fails = 0, locked_until = null, updated_at = now();
end $$;
revoke all on function public.set_desk_pin(text) from public, anon, authenticated;
grant execute on function public.set_desk_pin(text) to authenticated;

-- Same as 0034 apart from: the code is a pass, used up by the scan; a used, expired or replaced pass, or an old permanent code,
-- is 'old_code' and names nobody (the log still records whose it was).
create or replace function public.scan_member(p_code text) returns jsonb
language plpgsql security definer set search_path='' as $$
declare
  v_code text := left(trim(coalesce(p_code, '')), 100);
  v_pass public.member_passes; v_logged uuid;
  v_member uuid; v_name text; v_active boolean; v_status text; v_expires timestamptz;
begin
  perform public.require_permission('check_ins:record');
  select * into v_pass from public.member_passes where code = v_code for update;
  if found and v_pass.scanned_at is null and v_pass.expires_at > now() then
    update public.member_passes set scanned_at = now(), scanned_by = auth.uid() where pass_id = v_pass.pass_id;
    select id, coalesce(nullif(trim(full_name), ''), username), is_active into v_member, v_name, v_active from public.profiles where id = v_pass.member_id;
  else
    v_logged := coalesce(v_pass.member_id, (select user_id from public.member_codes where code = v_code));
  end if;

  if v_member is null then
    v_status := case when v_logged is null then 'not_found' else 'old_code' end;
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

  insert into public.member_scans (scanned_by, member_id, code_seen, result) values (auth.uid(), coalesce(v_member, v_logged), v_code, v_status);
  return jsonb_build_object('result', v_status, 'member', v_member, 'name', v_name, 'expires', v_expires);
end $$;

-- Same as 0044 apart from: no method means qr; qr needs this desk's fresh scan; manual and offline need the PIN, or an
-- Admin or System admin with a reason.
drop function public.record_check_in(uuid, text, timestamptz, uuid);
create function public.record_check_in(p_member_id uuid, p_method text default null, p_at timestamptz default null, p_client_ref uuid default null,
  p_pin text default null, p_reason text default null) returns bigint
language plpgsql security definer set search_path='' as $$
declare
  v_id bigint; v_at timestamptz := coalesce(p_at, now()); v_method text := coalesce(p_method, 'qr');
  v_reason text := nullif(btrim(p_reason), ''); v_pass bigint; v_pin public.desk_pins;
begin
  perform public.require_permission('check_ins:record');
  if v_method not in ('qr','manual','offline') then perform public.fail('bad_input','22023'); end if;
  -- only a queued offline check-in may name its own time, within the copy's 24 hours plus a day to get back online
  if p_at is not null and (v_method <> 'offline' or p_at > now()+interval '5 minutes' or p_at < now()-interval '48 hours') then
    perform public.fail('bad_input','22023');
  end if;
  if char_length(v_reason) > 500 then perform public.fail('bad_reason','22023'); end if;
  if p_client_ref is not null then
    select check_in_id into v_id from public.gym_check_ins where client_ref=p_client_ref;
    if found then return v_id; end if;
  end if;
  if not exists (select 1 from public.members where user_id=p_member_id) then perform public.fail('not_found','22023'); end if;
  if p_at is null then
    if not public.is_active_member(p_member_id) then perform public.fail('no_active_membership','22023'); end if;
  elsif not exists (select 1 from public.membership_terms t join public.profiles p on p.id=t.member_id
      where t.member_id=p_member_id and p.is_active and t.cancelled_at is null and t.starts_at<=v_at and t.ends_at>v_at) then
    perform public.fail('no_active_membership','22023');
  end if;

  if v_method = 'qr' then
    select pass_id into v_pass from public.member_passes
      where member_id=p_member_id and scanned_by=auth.uid() and scanned_at > now()-interval '10 minutes' and check_in_id is null
      order by scanned_at desc limit 1 for update;
    if v_pass is null then perform public.fail('scan_required','22023'); end if;
  elsif p_pin is null then
    if v_reason is null or not (public.has_role('admin') or public.has_role('subadmin')) then perform public.fail('pin_required','22023'); end if;
  else
    select * into v_pin from public.desk_pins where user_id=p_member_id for update;
    if not found then perform public.fail('no_desk_pin','22023'); end if;
    if v_pin.locked_until > now() then perform public.fail('pin_locked','22023'); end if;
    if extensions.crypt(p_pin, v_pin.pin_hash) <> v_pin.pin_hash then
      -- raising would roll the count back, so a wrong PIN returns null instead; the app reads null as a wrong PIN
      update public.desk_pins set fails = case when fails >= 4 then 0 else fails + 1 end,
        locked_until = case when fails >= 4 then now() + interval '15 minutes' else locked_until end where user_id=p_member_id;
      return null;
    end if;
    update public.desk_pins set fails = 0 where user_id=p_member_id;
  end if;

  insert into public.gym_check_ins (member_id, recorded_by, checked_in_at, method, client_ref, reason)
    values (p_member_id, auth.uid(), v_at, v_method, p_client_ref, v_reason) returning check_in_id into v_id;
  if v_pass is not null then update public.member_passes set check_in_id=v_id where pass_id=v_pass; end if;
  return v_id;
end $$;
revoke all on function public.record_check_in(uuid,text,timestamptz,uuid,text,text) from public, anon;
grant execute on function public.record_check_in(uuid,text,timestamptz,uuid,text,text) to authenticated;

-- Same as 0044 apart from: no codes (a pass can't be checked offline).
create or replace function public.desk_member_snapshot() returns jsonb
language plpgsql stable security definer set search_path='' as $$
begin
  perform public.require_permission('check_ins:record');
  return coalesce((
    select jsonb_agg(jsonb_build_object('member', m.user_id, 'name', p.full_name,
      'terms', coalesce((select jsonb_agg(jsonb_build_object('starts', t.starts_at, 'ends', t.ends_at))
                         from public.membership_terms t where t.member_id=m.user_id and t.cancelled_at is null and t.ends_at>now()), '[]'::jsonb)))
    from public.members m join public.profiles p on p.id=m.user_id
    where p.is_active), '[]'::jsonb);
end $$;
