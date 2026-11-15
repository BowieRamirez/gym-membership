-- Phase 5 of the 2026-11-12 plan (owner decision G12): login mode. A member at the desk with no phone for the pass and no desk PIN
-- types their own account password on the desk screen. The Edge Function verify-member-password (signed-in desk staff only) asks
-- desk_password_target for the member's sign-in email, checks the password with Supabase Auth in a separate client, and reports the
-- outcome to desk_password_result. A right password gives a one-use proof for 2 minutes that record_check_in accepts with method
-- 'password'. Wrong passwords count with the desk PIN's tries (0049): the fifth wrong try of either locks the member's check-in by
-- name for 15 minutes. The password itself never reaches the database. Members only (G12).

-- a member without a desk PIN still needs a row to count wrong passwords; record_check_in treats a null hash as no PIN
alter table public.desk_pins alter column pin_hash drop not null;

alter table public.gym_check_ins drop constraint gym_check_ins_method_check,
  add constraint gym_check_ins_method_check check (method in ('qr', 'manual', 'offline', 'password'));

create table public.desk_password_proofs (
  proof      uuid        primary key default gen_random_uuid(),
  member_id  uuid        not null references public.members (user_id) on delete cascade,
  desk_id    uuid        not null references public.profiles (id),
  expires_at timestamptz not null default now() + interval '2 minutes',
  used_at    timestamptz
);
alter table public.desk_password_proofs enable row level security;
revoke all on public.desk_password_proofs from public, anon, authenticated;

-- The Edge Function names the desk from the caller's checked token, so these can't use auth.uid(); they check what
-- require_permission('check_ins:record') checks, for that account.
create function public.desk_may_check_in(p_desk uuid) returns boolean
language sql stable security definer set search_path='' as $$
  select coalesce((select is_active from public.profiles where id = p_desk), false) and exists (
    select 1 from public.user_roles ur
    join public.role_permissions rp on rp.role_id = ur.role_id
    join public.permissions p on p.permission_id = rp.permission_id
    where ur.user_id = p_desk and p.resource || ':' || p.action = 'check_ins:record');
$$;
revoke all on function public.desk_may_check_in(uuid) from public, anon, authenticated;

create function public.desk_password_target(p_desk uuid, p_member uuid) returns text
language plpgsql security definer set search_path='' as $$
declare v_email text;
begin
  if not public.desk_may_check_in(p_desk) then perform public.fail('forbidden', '42501'); end if;
  select u.email into v_email from public.members m join auth.users u on u.id = m.user_id where m.user_id = p_member;
  if v_email is null then perform public.fail('not_found', '22023'); end if;
  if exists (select 1 from public.desk_pins where user_id = p_member and locked_until > now()) then perform public.fail('pin_locked', '22023'); end if;
  return v_email;
end $$;
revoke all on function public.desk_password_target(uuid, uuid) from public, anon, authenticated;
grant execute on function public.desk_password_target(uuid, uuid) to service_role;

-- {proof} for a right password; {error: wrong_password} or, when this try locks the member, {error: pin_locked}
create function public.desk_password_result(p_desk uuid, p_member uuid, p_ok boolean) returns jsonb
language plpgsql security definer set search_path='' as $$
declare v_proof uuid; v_locked timestamptz;
begin
  if not public.desk_may_check_in(p_desk) then perform public.fail('forbidden', '42501'); end if;
  if not exists (select 1 from public.members where user_id = p_member) then perform public.fail('not_found', '22023'); end if;
  if exists (select 1 from public.desk_pins where user_id = p_member and locked_until > now()) then perform public.fail('pin_locked', '22023'); end if;
  if p_ok then
    update public.desk_pins set fails = 0 where user_id = p_member;
    insert into public.desk_password_proofs (member_id, desk_id) values (p_member, p_desk) returning proof into v_proof;
    return jsonb_build_object('proof', v_proof);
  end if;
  -- the same count and lock as a wrong PIN in record_check_in (0049)
  insert into public.desk_pins (user_id, pin_hash) values (p_member, null) on conflict (user_id) do nothing;
  update public.desk_pins set fails = case when fails >= 4 then 0 else fails + 1 end,
    locked_until = case when fails >= 4 then now() + interval '15 minutes' else locked_until end
    where user_id = p_member returning locked_until into v_locked;
  return jsonb_build_object('error', case when v_locked > now() then 'pin_locked' else 'wrong_password' end);
end $$;
revoke all on function public.desk_password_result(uuid, uuid, boolean) from public, anon, authenticated;
grant execute on function public.desk_password_result(uuid, uuid, boolean) to service_role;

-- Same as 0049 apart from: method 'password' with a proof from desk_password_result (this desk, this member, unused, not expired),
-- and a desk_pins row with no hash counts as no PIN.
drop function public.record_check_in(uuid, text, timestamptz, uuid, text, text);
create function public.record_check_in(p_member_id uuid, p_method text default null, p_at timestamptz default null, p_client_ref uuid default null,
  p_pin text default null, p_reason text default null, p_proof uuid default null) returns bigint
language plpgsql security definer set search_path='' as $$
declare
  v_id bigint; v_at timestamptz := coalesce(p_at, now()); v_method text := coalesce(p_method, 'qr');
  v_reason text := nullif(btrim(p_reason), ''); v_pass bigint; v_pin public.desk_pins; v_proof uuid;
begin
  perform public.require_permission('check_ins:record');
  if v_method not in ('qr','manual','offline','password') then perform public.fail('bad_input','22023'); end if;
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
  elsif v_method = 'password' then
    select proof into v_proof from public.desk_password_proofs
      where proof=p_proof and member_id=p_member_id and desk_id=auth.uid() and used_at is null and expires_at > now() for update;
    if v_proof is null then perform public.fail('proof_invalid','22023'); end if;
    update public.desk_password_proofs set used_at=now() where proof=v_proof;
  elsif p_pin is null then
    if v_reason is null or not (public.has_role('admin') or public.has_role('subadmin')) then perform public.fail('pin_required','22023'); end if;
  else
    select * into v_pin from public.desk_pins where user_id=p_member_id for update;
    if v_pin.locked_until > now() then perform public.fail('pin_locked','22023'); end if;
    if v_pin.pin_hash is null then perform public.fail('no_desk_pin','22023'); end if;
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
-- recreated, so Supabase's default privileges would open it to anon again: revoke explicitly
revoke all on function public.record_check_in(uuid,text,timestamptz,uuid,text,text,uuid) from public, anon;
grant execute on function public.record_check_in(uuid,text,timestamptz,uuid,text,text,uuid) to authenticated;
