-- Item 10 and the forgot-phone fix (2026-11-05, owner decision D12). Reverses "offline is never Active" from the 2026-10-20 plan, within limits:
-- the desk keeps a copy of who has paid, shows it as a saved list and never as Active, and queues check-ins. Sending the queue rechecks each
-- one here, against the membership on the day it happened. Known risk, accepted by the owner: a member who expired since the copy was made
-- can get in while the desk is offline; the server refuses the queued check-in afterwards, and staff are told.

alter table public.gym_check_ins
  add column method text check (method in ('qr','manual','offline')),   -- null: recorded before this existed
  add column client_ref uuid unique;                                    -- so resending the queue can't check someone in twice

-- The defaults keep installed apps, which send only p_member_id, working.
drop function public.record_check_in(uuid);
create function public.record_check_in(p_member_id uuid, p_method text default null, p_at timestamptz default null, p_client_ref uuid default null) returns bigint
language plpgsql security definer set search_path='' as $$
declare v_id bigint; v_at timestamptz := coalesce(p_at, now());
begin
  perform public.require_permission('check_ins:record');
  if p_method is not null and p_method not in ('qr','manual','offline') then perform public.fail('bad_input','22023'); end if;
  -- only a queued offline check-in may name its own time, within the copy's 24 hours plus a day to get back online
  if p_at is not null and (p_method is distinct from 'offline' or p_at > now()+interval '5 minutes' or p_at < now()-interval '48 hours') then
    perform public.fail('bad_input','22023');
  end if;
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
  insert into public.gym_check_ins (member_id, recorded_by, checked_in_at, method, client_ref)
    values (p_member_id, auth.uid(), v_at, p_method, p_client_ref) returning check_in_id into v_id;
  return v_id;
end $$;
revoke all on function public.record_check_in(uuid,text,timestamptz,uuid) from public, anon;
grant execute on function public.record_check_in(uuid,text,timestamptz,uuid) to authenticated;

-- What the desk saves for offline use: every active account's member code, name and paid terms still to come or running.
create function public.desk_member_snapshot() returns jsonb
language plpgsql stable security definer set search_path='' as $$
begin
  perform public.require_permission('check_ins:record');
  return coalesce((
    select jsonb_agg(jsonb_build_object('code', c.code, 'member', m.user_id, 'name', p.full_name,
      'terms', coalesce((select jsonb_agg(jsonb_build_object('starts', t.starts_at, 'ends', t.ends_at))
                         from public.membership_terms t where t.member_id=m.user_id and t.cancelled_at is null and t.ends_at>now()), '[]'::jsonb)))
    from public.members m join public.profiles p on p.id=m.user_id join public.member_codes c on c.user_id=m.user_id
    where p.is_active), '[]'::jsonb);
end $$;
revoke all on function public.desk_member_snapshot() from public, anon;
grant execute on function public.desk_member_snapshot() to authenticated;
