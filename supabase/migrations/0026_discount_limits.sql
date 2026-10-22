-- Phase 3 (D4/D5, 2026-10-22): discount codes get a use limit.
-- A place is held by a payment with the code that is pending, verified or voided; rejected, expired and
-- member-cancelled payments give it back. Usage is counted from payments (no counters to drift) while the
-- code row is locked, so two checkouts cannot both take the last place. When verified + voided uses reach
-- the limit the code is archived with 'Limit reached' and can never be restored; make a new code instead.
-- Codes made before this migration keep max_uses null (unlimited) until an admin gives them a limit.

alter table public.discounts add column max_uses int check (max_uses > 0);

create function public.discount_held(p_id bigint) returns bigint
language sql stable security definer set search_path='' as $$
  select count(*) from public.payments where discount_id=p_id and status in ('pending','verified','voided');
$$;
create function public.discount_consumed(p_id bigint) returns bigint
language sql stable security definer set search_path='' as $$
  select count(*) from public.payments where discount_id=p_id and status in ('verified','voided');
$$;
revoke all on function public.discount_held(bigint),public.discount_consumed(bigint) from public,anon,authenticated;

-- Limit rules for every write to a code: required on new codes, never back to unlimited, never below the
-- places in use; a used-up code archives itself and refuses restore. Fires after discounts_archive_guard.
create function public.guard_discount_limit() returns trigger
language plpgsql security definer set search_path='' as $$
declare v_consumed bigint;
begin
  if tg_op='INSERT' and new.max_uses is null then raise exception 'limit_required' using errcode='22023'; end if;
  if tg_op='UPDATE' and new.max_uses is distinct from old.max_uses then
    if new.max_uses is null then raise exception 'limit_required' using errcode='22023'; end if;
    if new.max_uses<public.discount_held(new.discount_id) then raise exception 'limit_below_used' using errcode='22023'; end if;
  end if;
  if new.max_uses is null or tg_op='INSERT' then return new; end if;
  v_consumed := public.discount_consumed(new.discount_id);
  if not old.is_active and new.is_active and v_consumed>=new.max_uses then
    raise exception 'record_not_restorable' using errcode='P0002';
  end if;
  if new.is_active and v_consumed>=new.max_uses then
    new.is_active := false; new.archived_at := now(); new.archived_by := auth.uid(); new.archive_reason := 'Limit reached';
  end if;
  return new;
end $$;
revoke all on function public.guard_discount_limit() from public,anon,authenticated;
create trigger discounts_limit_guard before insert or update on public.discounts for each row execute function public.guard_discount_limit();

-- A payment that becomes verified (or is recorded verified at the counter) may use up the last place.
create function public.archive_used_up_discount() returns trigger
language plpgsql security definer set search_path='' as $$
declare v_disc public.discounts;
begin
  if new.discount_id is null or new.status not in ('verified','voided') then return null; end if;
  select * into v_disc from public.discounts where discount_id=new.discount_id for update;
  if v_disc.is_active and v_disc.expires_at>now() and public.discount_consumed(v_disc.discount_id)>=v_disc.max_uses then
    update public.discounts set is_active=false,archive_reason='Limit reached' where discount_id=v_disc.discount_id;
  end if;
  return null;
end $$;
revoke all on function public.archive_used_up_discount() from public,anon,authenticated;
create trigger payments_discount_limit after insert or update of status on public.payments for each row execute function public.archive_used_up_discount();

-- Same as 0003 apart from: the code row is locked, voided uses also count as the member's one use, and the limit is checked.
create or replace function public.apply_discount(p_member uuid, p_code text, p_price numeric)
returns table (discount_id bigint, discount_amount numeric, amount numeric)
language plpgsql security definer
set search_path = ''
as $$
declare
	v_disc public.discounts;
	v_cut numeric;
begin
	if p_code is null or btrim(p_code) = '' then
		return query select null::bigint, 0::numeric, p_price;
		return;
	end if;

	select * into v_disc
	from public.discounts d
	where upper(d.code) = upper(btrim(p_code))
	  and d.is_active and d.starts_at <= now() and d.expires_at > now()
	for update;

	if not found or exists (
		select 1 from public.payments p
		where p.member_id = p_member and p.discount_id = v_disc.discount_id and p.status in ('pending', 'verified', 'voided')
	) then
		perform public.fail('invalid_code', '22023');
	end if;
	if v_disc.max_uses is not null and public.discount_held(v_disc.discount_id) >= v_disc.max_uses then
		perform public.fail('code_limit_reached', 'P0002');
	end if;

	v_cut := round(p_price * v_disc.percent / 100.0, 2);
	return query select v_disc.discount_id, v_cut, p_price - v_cut;
end;
$$;

-- Places in use per code for the Discounts tab: used = verified + voided, pending = held by unverified payments.
create function public.discount_usage() returns table(id bigint, used bigint, pending bigint)
language plpgsql stable security definer set search_path='' as $$
begin
  perform public.require_permission('discounts:manage');
  return query select p.discount_id, count(*) filter (where p.status in ('verified','voided')), count(*) filter (where p.status='pending')
    from public.payments p where p.discount_id is not null group by p.discount_id;
end $$;
revoke all on function public.discount_usage() from public,anon,authenticated;
grant execute on function public.discount_usage() to authenticated;

-- Same as 0024 apart from: a used-up code offers no Restore.
create or replace function public.archive_records(p_type text default '',p_status text default '',p_from date default null,p_to date default null,p_reason text default '',p_offset integer default 0,p_limit integer default 31)
returns table(record_type text,record_id text,name text,status text,archived_at timestamptz,archived_by text,reason text,details text,can_restore boolean)
language plpgsql security definer set search_path='' as $$
begin
  perform public.require_archive_admin();
  if p_offset is null or p_offset<0 or p_limit is null or p_limit not between 1 and 101 or p_from>p_to
    or coalesce(p_type,'') not in ('','account','code','plan','amenity') or coalesce(p_status,'') not in ('','inactive','deleted','expired') then
    perform public.fail('bad_archive_filter','22023');
  end if;
  return query
  with records as (
    select 'account'::text as typ,p.id::text as rid,p.full_name as title,
      case when p.deleted_at is not null then 'deleted' else 'inactive' end as state,
      coalesce(p.deleted_at,a.created_at) as stamp,coalesce(p.deleted_by,a.actor_id)::text as actor,
      coalesce(p.deletion_reason,case when a.created_at is not null then 'Deactivated account' end,'Unknown (legacy record)') as why,
      p.username as info,true as restorable
    from public.profiles p left join lateral (
      select e.created_at,e.actor_id from public.audit_log e where e.entity='profiles' and e.entity_id=p.id::text and e.action='user.deactivate' order by e.created_at desc limit 1
    ) a on true where not p.is_active
    union all
    select 'code',d.discount_id::text,d.code,case when d.expires_at<=now() then 'expired' else 'inactive' end,
      coalesce(d.archived_at,case when d.expires_at<=now() then d.expires_at end),d.archived_by::text,
      coalesce(d.archive_reason,case when d.expires_at<=now() then 'Expired code' end,'Unknown (legacy record)'),
      d.percent::text||'% discount; valid until '||to_char(d.expires_at at time zone 'Asia/Manila','YYYY-MM-DD HH24:MI')||' (Asia/Manila)',d.expires_at>now() and (d.max_uses is null or public.discount_consumed(d.discount_id)<d.max_uses)
    from public.discounts d where not d.is_active or d.expires_at<=now()
    union all
    select 'plan',p.membership_package_id::text,p.name,'inactive',p.archived_at,p.archived_by::text,coalesce(p.archive_reason,'Unknown (legacy record)'),
      'PHP '||p.price::text||'; '||p.duration_days::text||' days; '||coalesce(p.description,''),true from public.membership_packages p where not p.is_active
    union all
    select 'amenity',a.amenity_id::text,a.name,'inactive',a.archived_at,a.archived_by::text,coalesce(a.archive_reason,'Unknown (legacy record)'),
      'PHP '||a.price::text||'; '||coalesce(a.description,''),true from public.amenities a where not a.is_active
  )
  select r.typ,r.rid,r.title,r.state,r.stamp,r.actor,r.why,r.info,r.restorable from records r
  where (coalesce(p_type,'')='' or r.typ=p_type) and (coalesce(p_status,'')='' or r.state=p_status)
    and (p_from is null or (r.stamp at time zone 'Asia/Manila')::date>=p_from)
    and (p_to is null or (r.stamp at time zone 'Asia/Manila')::date<=p_to)
    and position(lower(coalesce(btrim(p_reason),'')) in lower(r.why))>0
  order by r.stamp desc nulls last,r.typ,r.rid limit p_limit offset p_offset;
end $$;
