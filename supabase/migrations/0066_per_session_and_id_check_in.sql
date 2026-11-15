-- Phase 8 of the 2026-11-12 plan (owner decisions G19-G21, 2026-11-14).
--
-- G19: amenities are sold per session only. Every sale (the member's purchase, the Cashier, a booking) inserts a user_amenities row,
-- so one trigger there refuses a new pass for an amenity still set up as a pass (duration_days set): `pass_not_sold`. Passes already
-- bought are untouched and keep booking until they end. Staff can't set up a pass (insert, or set duration_days on an amenity); a pass
-- amenity can still be turned into a per-session one (Sell per session clears duration_days). The database owner (no auth.uid(),
-- as 0024/0050) is not checked. Membership packages hold no amenities (no table links them), so there is nothing to add for that.
--
-- G20/G21: check-in by ID. record_check_in takes p_id_type (a fixed list); method 'id' online, or 'offline' for a queued one.
-- Whoever may record check-ins (front desk, Admin, System admin) may use it, for any active member, with or without a desk PIN.
-- Only the ID's type is kept (gym_check_ins.id_type), never its number or a photo. Every ID check-in is in the audit log.

-- ---------- G19 ----------
create function public.guard_no_new_pass() returns trigger
language plpgsql security definer set search_path='' as $$
begin
  if auth.uid() is null then return new; end if;
  if tg_table_name = 'user_amenities' then
    if exists (select 1 from public.amenities a where a.amenity_id = new.amenity_id and a.duration_days is not null) then
      perform public.fail('pass_not_sold', 'P0002');
    end if;
  elsif new.duration_days is not null and (tg_op = 'INSERT' or new.duration_days is distinct from old.duration_days) then
    perform public.fail('pass_not_sold', '22023');
  end if;
  return new;
end $$;
revoke all on function public.guard_no_new_pass() from public, anon, authenticated;
create trigger user_amenities_no_new_pass before insert on public.user_amenities for each row execute function public.guard_no_new_pass();
create trigger amenities_no_new_pass before insert or update of duration_days on public.amenities for each row execute function public.guard_no_new_pass();

-- ---------- G20 / G21 ----------
alter table public.gym_check_ins
  add column id_type text check (id_type in ('drivers_license', 'national_id', 'passport', 'school_id', 'other')),
  drop constraint gym_check_ins_method_check,
  add constraint gym_check_ins_method_check check (method in ('qr', 'manual', 'offline', 'password', 'id'));

-- Same as 0062 with 0063's has_desk_pin line, apart from p_id_type: method 'id', or a queued 'offline' one, with a type and no secret.
drop function public.record_check_in(uuid, text, timestamptz, uuid, text, text, uuid);
create function public.record_check_in(p_member_id uuid, p_method text default null, p_at timestamptz default null, p_client_ref uuid default null,
  p_pin text default null, p_reason text default null, p_proof uuid default null, p_id_type text default null) returns bigint
language plpgsql security definer set search_path='' as $$
declare
  v_id bigint; v_at timestamptz := coalesce(p_at, now()); v_method text := coalesce(p_method, 'qr');
  v_reason text := nullif(btrim(p_reason), ''); v_pass bigint; v_pin public.desk_pins; v_proof uuid;
begin
  perform public.require_permission('check_ins:record');
  if v_method not in ('qr','manual','offline','password','id') then perform public.fail('bad_input','22023'); end if;
  if (v_method = 'id') <> (p_id_type is not null) and not (v_method = 'offline' and p_id_type is not null) then
    perform public.fail('bad_input','22023');
  end if;
  if p_id_type not in ('drivers_license','national_id','passport','school_id','other') then perform public.fail('bad_id_type','22023'); end if;
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

  if p_id_type is not null then
    null;   -- G20: the desk looked at the ID; there is no secret to check
  elsif v_method = 'qr' then
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
    if exists (select 1 from public.desk_pins where user_id=p_member_id and pin_hash is not null) then perform public.fail('has_desk_pin','22023'); end if;
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

  insert into public.gym_check_ins (member_id, recorded_by, checked_in_at, method, client_ref, reason, id_type)
    values (p_member_id, auth.uid(), v_at, v_method, p_client_ref, v_reason, p_id_type) returning check_in_id into v_id;
  if v_pass is not null then update public.member_passes set check_in_id=v_id where pass_id=v_pass; end if;
  if p_id_type is not null then
    perform public.audit('check_in.id', 'gym_check_ins', v_id::text, jsonb_build_object('member', p_member_id, 'id_type', p_id_type, 'at', v_at));
  end if;
  return v_id;
end $$;
-- recreated, so Supabase's default privileges would open it to anon again: revoke explicitly
revoke all on function public.record_check_in(uuid,text,timestamptz,uuid,text,text,uuid,text) from public, anon;
grant execute on function public.record_check_in(uuid,text,timestamptz,uuid,text,text,uuid,text) to authenticated;
