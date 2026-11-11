-- 0045: audit log table (Phase 8, items 5 and 14, D7).
-- Each new entry records the actor's role and network address; older entries stay blank rather than guessed.
-- Addresses are personal data, so they are cleared after 90 days; the rest of the entry stays.
-- The app reads the log one page at a time through audit_records instead of loading the whole table.

alter table public.audit_log add column ip_address text, add column role text;
comment on column public.audit_log.ip_address is 'Caller network address at the time (D7); cleared after 90 days by clear_old_audit_ips';
comment on column public.audit_log.role is 'Actor''s main role code at the time: admin, subadmin, employee, coach or member; null for the system';

-- The caller's address as the API gateway passed it. Cloudflare sets cf-connecting-ip and refuses a forged one;
-- the first x-forwarded-for entry is the fallback. Null outside an API request (cron, migrations), and null rather than an
-- error if the headers can't be read, because every audited action runs through here.
create function public.request_ip() returns text
language plpgsql stable
set search_path = ''
as $$
declare h json;
begin
	h := nullif(current_setting('request.headers', true), '')::json;
	return nullif(left(btrim(coalesce(h ->> 'cf-connecting-ip', split_part(h ->> 'x-forwarded-for', ',', 1), '')), 64), '');
exception when others then
	return null;
end;
$$;

-- The one role that best describes a person, in the same order as the app's RoleResolver.
create function public.main_role(p_user uuid) returns text
language sql stable security definer
set search_path = ''
as $$
	select r.name from public.user_roles ur join public.roles r on r.role_id = ur.role_id
	where ur.user_id = p_user
	order by array_position(array['admin','subadmin','employee','coach','member'], r.name) nulls last
	limit 1;
$$;

create or replace function public.audit(p_action text, p_entity text, p_entity_id text default null, p_details jsonb default null)
returns void
language sql security definer
set search_path = ''
as $$
	insert into public.audit_log (actor_id, action, entity, entity_id, details, ip_address, role)
	values (auth.uid(), p_action, p_entity, p_entity_id, p_details, public.request_ip(), public.main_role(auth.uid()));
$$;

-- One page of the log, newest first, for staff with audit:read. Filters: Manila dates, part of the actor's name
-- ("System" for entries with no actor), the area before the dot in the action, and the object's table. An account or role
-- entry also gets the person's name.
-- p_limit goes up to 1001 so an export can read in large pages; the screen asks for 11 to know if there is a next page.
create function public.audit_records(p_from date default null, p_to date default null, p_user text default '', p_area text default '',
	p_entity text default '', p_offset integer default 0, p_limit integer default 11)
returns table(audit_id bigint, created_at timestamptz, actor_name text, role text, ip_address text, action text, entity text,
	entity_id text, object_name text, details jsonb)
language plpgsql stable security definer
set search_path = ''
as $$
#variable_conflict use_column
begin
	perform public.require_permission('audit:read');
	if p_offset is null or p_offset < 0 or p_limit is null or p_limit not between 1 and 1001 or p_from > p_to
		or length(coalesce(p_user, '')) > 100 or length(coalesce(p_area, '')) > 40 or length(coalesce(p_entity, '')) > 40 then
		perform public.fail('bad_audit_filter', '22023');
	end if;
	return query
	select a.audit_id, a.created_at, coalesce(p.full_name, 'System'), a.role, a.ip_address, a.action, a.entity, a.entity_id,
		o.full_name, a.details
	from public.audit_log a
	left join public.profiles p on p.id = a.actor_id
	left join public.profiles o on a.entity in ('profiles', 'user_roles') and o.id::text = a.entity_id   -- both keep the person's id
	where (p_from is null or (a.created_at at time zone 'Asia/Manila')::date >= p_from)
		and (p_to is null or (a.created_at at time zone 'Asia/Manila')::date <= p_to)
		and position(lower(btrim(coalesce(p_user, ''))) in lower(coalesce(p.full_name, 'System'))) > 0
		and (coalesce(p_area, '') = '' or split_part(a.action, '.', 1) = p_area)
		and (coalesce(p_entity, '') = '' or a.entity = p_entity)
	order by a.created_at desc, a.audit_id desc
	limit p_limit offset p_offset;
end;
$$;


-- D7: addresses older than 90 days are cleared every night (00:20 Manila).
create function public.clear_old_audit_ips() returns integer
language sql security definer
set search_path = ''
as $$
	with cleared as (
		update public.audit_log set ip_address = null
		where ip_address is not null and created_at < now() - interval '90 days'
		returning 1)
	select count(*)::int from cleared;
$$;
select cron.schedule('clear-audit-ips', '20 16 * * *', $$select public.clear_old_audit_ips()$$);

revoke all on function public.request_ip(), public.main_role(uuid), public.clear_old_audit_ips() from public, anon, authenticated;
revoke all on function public.audit_records(date, date, text, text, text, integer, integer) from public, anon, authenticated;
grant execute on function public.audit_records(date, date, text, text, text, integer, integer) to authenticated;
