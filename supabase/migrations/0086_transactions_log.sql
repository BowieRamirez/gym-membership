-- Plan 2026-11-17 Z4-Z6: the audit log page splits into Transactions (payment.*, refund.*, cash_shift.*) and the rest.
-- p_money: true = money rows only, false = the rest, null (old apps, which don't send it) = every row as before.
-- Each row also says who the customer was (the payment's member, or the Day pass name), the amount, and how they paid
-- (Xendit's channel when it logged one, otherwise the payment's method).
drop function public.audit_records(date, date, text, text, text, integer, integer);
create function public.audit_records(p_from date default null, p_to date default null, p_user text default '', p_area text default '',
	p_entity text default '', p_offset integer default 0, p_limit integer default 11, p_money boolean default null)
returns table(audit_id bigint, created_at timestamptz, actor_name text, role text, ip_address text, action text, entity text,
	entity_id text, object_name text, details jsonb, customer text, amount numeric, paid_with text)
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
		o.full_name, a.details,
		coalesce(c.full_name, py.walk_in_name), coalesce(rr.amount, py.amount), coalesce(a.details ->> 'channel', py.method::text)
	from public.audit_log a
	left join public.profiles p on p.id = a.actor_id
	left join public.profiles o on a.entity in ('profiles', 'user_roles') and o.id::text = a.entity_id   -- both keep the person's id
	left join public.refund_requests rr on a.entity = 'refund_requests' and rr.refund_request_id::text = a.entity_id
	left join public.payments py on py.payment_id = coalesce(rr.payment_id, case when a.entity = 'payments' and a.entity_id ~ '^\d+$' then a.entity_id::bigint end)
	left join public.profiles c on c.id = py.member_id
	where (p_from is null or (a.created_at at time zone 'Asia/Manila')::date >= p_from)
		and (p_to is null or (a.created_at at time zone 'Asia/Manila')::date <= p_to)
		and position(lower(btrim(coalesce(p_user, ''))) in lower(coalesce(p.full_name, 'System'))) > 0
		and (coalesce(p_area, '') = '' or split_part(a.action, '.', 1) = p_area)
		and (coalesce(p_entity, '') = '' or a.entity = p_entity)
		and (p_money is null or (split_part(a.action, '.', 1) in ('payment', 'refund', 'cash_shift')) = p_money)
	order by a.created_at desc, a.audit_id desc
	limit p_limit offset p_offset;
end;
$$;
revoke all on function public.audit_records(date, date, text, text, text, integer, integer, boolean) from public, anon, authenticated;
grant execute on function public.audit_records(date, date, text, text, text, integer, integer, boolean) to authenticated;
