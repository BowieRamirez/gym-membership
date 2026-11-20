-- Owner fixes 2026-11-19 Q4 (docs/superpowers/plans/2026-11-19-owner-fixes.md): Transactions has its own filters, apart from the
-- audit log's: the customer (name), what was bought (membership, amenity, day_pass, refund, cash_shift), how it was paid (the Xendit
-- channel or the payment's method) and the status (pending, paid, failed, refunded, voided). Dates and Recorded by (p_user) stay.
-- Every new parameter has a default, so older apps that don't send them get what they got before.
drop function public.audit_records(date, date, text, text, text, integer, integer, boolean);
create function public.audit_records(p_from date default null, p_to date default null, p_user text default '', p_area text default '',
	p_entity text default '', p_offset integer default 0, p_limit integer default 11, p_money boolean default null,
	p_customer text default '', p_what text default '', p_paid_with text default '', p_status text default '')
returns table(audit_id bigint, created_at timestamptz, actor_name text, role text, ip_address text, action text, entity text,
	entity_id text, object_name text, details jsonb, customer text, amount numeric, paid_with text)
language plpgsql stable security definer
set search_path = ''
as $$
#variable_conflict use_column
begin
	perform public.require_permission('audit:read');
	if p_offset is null or p_offset < 0 or p_limit is null or p_limit not between 1 and 1001 or p_from > p_to
		or length(coalesce(p_user, '')) > 100 or length(coalesce(p_area, '')) > 40 or length(coalesce(p_entity, '')) > 40
		or length(coalesce(p_customer, '')) > 100 or length(coalesce(p_paid_with, '')) > 20
		or coalesce(p_what, '') not in ('', 'membership', 'amenity', 'day_pass', 'refund', 'cash_shift')
		or coalesce(p_status, '') not in ('', 'pending', 'paid', 'failed', 'refunded', 'voided') then
		perform public.fail('bad_audit_filter', '22023');
	end if;
	return query
	select x.audit_id, x.created_at, x.actor_name, x.role, x.ip_address, x.action, x.entity, x.entity_id, x.object_name, x.details,
		x.customer, x.amount, x.paid_with
	from (
		select a.audit_id, a.created_at, coalesce(p.full_name, 'System') actor_name, a.role, a.ip_address, a.action, a.entity, a.entity_id,
			o.full_name object_name, a.details,
			coalesce(c.full_name, py.walk_in_name) customer, coalesce(rr.amount, py.amount) amount, coalesce(a.details ->> 'channel', py.method::text) paid_with,
			split_part(a.action, '.', 1) area, py.kind, py.status::text pay_status, mp.is_day_pass,
			exists(select 1 from public.refund_requests f where f.payment_id = py.payment_id and f.status = 'paid') refunded
		from public.audit_log a
		left join public.profiles p on p.id = a.actor_id
		left join public.profiles o on a.entity in ('profiles', 'user_roles') and o.id::text = a.entity_id   -- both keep the person's id
		left join public.refund_requests rr on a.entity = 'refund_requests' and rr.refund_request_id::text = a.entity_id
		left join public.payments py on py.payment_id = coalesce(rr.payment_id, case when a.entity = 'payments' and a.entity_id ~ '^\d+$' then a.entity_id::bigint end)
		left join public.profiles c on c.id = py.member_id
		left join public.membership_packages mp on mp.membership_package_id = py.membership_package_snapshot_id
		where (p_from is null or (a.created_at at time zone 'Asia/Manila')::date >= p_from)
			and (p_to is null or (a.created_at at time zone 'Asia/Manila')::date <= p_to)
			and position(lower(btrim(coalesce(p_user, ''))) in lower(coalesce(p.full_name, 'System'))) > 0
			and (coalesce(p_area, '') = '' or split_part(a.action, '.', 1) = p_area)
			and (coalesce(p_entity, '') = '' or a.entity = p_entity)
			and (p_money is null or (split_part(a.action, '.', 1) in ('payment', 'refund', 'cash_shift')) = p_money)
	) x
	where position(lower(btrim(coalesce(p_customer, ''))) in lower(coalesce(x.customer, ''))) > 0
		-- Xendit's channel (GCASH, or PH_GCASH from its dashboard; PAYMAYA, QRPH, CARDS) or the payment's method (cash, gcash, qr)
		and (coalesce(p_paid_with, '') = '' or lower(regexp_replace(x.paid_with, '^PH_', '')) = lower(btrim(p_paid_with)))
		and (coalesce(p_what, '') = ''
			or p_what = 'refund' and x.area = 'refund'
			or p_what = 'cash_shift' and x.area = 'cash_shift'
			or p_what = 'day_pass' and x.area = 'payment' and (x.kind = 'walk_in' or coalesce(x.is_day_pass, false))
			or p_what = 'membership' and x.area = 'payment' and x.kind in ('membership', 'renewal') and not coalesce(x.is_day_pass, false)
			or p_what = 'amenity' and x.area = 'payment' and x.kind = 'amenity')
		and (coalesce(p_status, '') = ''
			or p_status = 'pending' and x.pay_status = 'pending'
			or p_status = 'paid' and x.pay_status = 'verified' and not x.refunded
			or p_status = 'refunded' and x.pay_status = 'verified' and x.refunded
			or p_status = 'failed' and x.pay_status in ('rejected', 'expired')
			or p_status = 'voided' and x.pay_status = 'voided')
	order by x.created_at desc, x.audit_id desc
	limit p_limit offset p_offset;
end;
$$;
revoke all on function public.audit_records(date, date, text, text, text, integer, integer, boolean, text, text, text, text) from public, anon, authenticated;
grant execute on function public.audit_records(date, date, text, text, text, integer, integer, boolean, text, text, text, text) to authenticated;
