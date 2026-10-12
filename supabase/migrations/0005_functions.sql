-- 0005: server-side functions (plan 7 and 8)
--
-- Everything that touches more than one row, money, roles or status lives here.
-- All are security definer with an empty search_path, check who is calling first,
-- and use the error tokens from 0003. Privileges are set at the end of 0006.

-- =====================================================================
-- Payments: shared pieces
-- =====================================================================

-- Plan 8.4 steps 2-4: turn on whatever a verified payment paid for.
create function public.activate_payment_target(p_payment_id bigint) returns void
language plpgsql security definer
set search_path = ''
as $$
declare
	v_pay public.payments;
	v_days int;
	v_ren public.membership_renewals;
begin
	select * into v_pay from public.payments where payment_id = p_payment_id;

	if v_pay.user_membership_package_id is not null then
		select pk.duration_days into v_days
		from public.user_membership_packages ump
		join public.membership_packages pk on pk.membership_package_id = ump.membership_package_id
		where ump.user_membership_package_id = v_pay.user_membership_package_id;

		update public.user_membership_packages
		set status = 'active', starts_at = now(), ends_at = now() + make_interval(days => v_days)
		where user_membership_package_id = v_pay.user_membership_package_id;

	elsif v_pay.renewal_id is not null then
		select * into v_ren from public.membership_renewals where renewal_id = v_pay.renewal_id;

		update public.user_membership_packages
		set status = 'active', ends_at = v_ren.new_ends_at
		where user_membership_package_id = v_ren.user_membership_package_id;

		update public.membership_renewals
		set status = 'completed', completed_at = now()
		where renewal_id = v_ren.renewal_id;

	elsif v_pay.user_amenity_id is not null then
		select a.duration_days into v_days
		from public.user_amenities ua
		join public.amenities a on a.amenity_id = ua.amenity_id
		where ua.user_amenity_id = v_pay.user_amenity_id;

		update public.user_amenities
		set status = 'active', expires_at = case when v_days is null then null else now() + make_interval(days => v_days) end
		where user_amenity_id = v_pay.user_amenity_id;
	end if;
end;
$$;

-- Unpaid or rejected: whatever the payment was going to switch on is cancelled.
create function public.cancel_payment_target(p_payment_id bigint) returns void
language plpgsql security definer
set search_path = ''
as $$
declare
	v_pay public.payments;
begin
	select * into v_pay from public.payments where payment_id = p_payment_id;

	update public.user_membership_packages set status = 'cancelled'
	where user_membership_package_id = v_pay.user_membership_package_id and status = 'pending';

	update public.membership_renewals set status = 'cancelled'
	where renewal_id = v_pay.renewal_id and status = 'pending';

	update public.user_amenities set status = 'cancelled'
	where user_amenity_id = v_pay.user_amenity_id and status = 'pending';
end;
$$;

-- =====================================================================
-- 7.1 Memberships, amenities, payments
-- =====================================================================

create function public.avail_membership(p_package_id bigint, p_discount_code text default null)
returns public.payments
language plpgsql security definer
set search_path = ''
as $$
declare
	v_uid uuid := auth.uid();
	v_pkg public.membership_packages;
	v_disc record;
	v_ump bigint;
	v_pay public.payments;
begin
	perform public.require_active_user();
	if not exists (select 1 from public.members where user_id = v_uid) then
		perform public.fail('forbidden', '42501');
	end if;

	select * into v_pkg from public.membership_packages where membership_package_id = p_package_id and is_active;
	if not found then perform public.fail('not_found', '22023'); end if;

	if exists (select 1 from public.user_membership_packages where member_id = v_uid and status in ('pending', 'active')) then
		perform public.fail('already_availed', 'P0002');
	end if;

	select * into v_disc from public.apply_discount(v_uid, p_discount_code, v_pkg.price);

	insert into public.user_membership_packages (member_id, membership_package_id)
	values (v_uid, v_pkg.membership_package_id)
	returning user_membership_package_id into v_ump;

	insert into public.payments (payment_code, kind, method, amount, discount_id, discount_amount, member_id, user_membership_package_id, expires_at)
	values (
		public.generate_payment_code(), 'membership', 'qr', v_disc.amount, v_disc.discount_id, v_disc.discount_amount, v_uid, v_ump,
		now() + make_interval(hours => (select payment_expiry_hours from public.gym_settings where id = 1))
	)
	returning * into v_pay;

	return v_pay;
end;
$$;

create function public.avail_amenity(p_amenity_id bigint, p_discount_code text default null)
returns public.payments
language plpgsql security definer
set search_path = ''
as $$
declare
	v_uid uuid := auth.uid();
	v_am public.amenities;
	v_disc record;
	v_ua bigint;
	v_pay public.payments;
begin
	perform public.require_active_user();
	if not public.is_active_member(v_uid) then perform public.fail('no_active_membership', '22023'); end if;

	select * into v_am from public.amenities where amenity_id = p_amenity_id and is_active;
	if not found then perform public.fail('not_found', '22023'); end if;

	if exists (select 1 from public.user_amenities where member_id = v_uid and amenity_id = p_amenity_id and status in ('pending', 'active')) then
		perform public.fail('already_availed', 'P0002');
	end if;

	select * into v_disc from public.apply_discount(v_uid, p_discount_code, v_am.price);

	insert into public.user_amenities (member_id, amenity_id)
	values (v_uid, v_am.amenity_id)
	returning user_amenity_id into v_ua;

	insert into public.payments (payment_code, kind, method, amount, discount_id, discount_amount, member_id, user_amenity_id, expires_at)
	values (
		public.generate_payment_code(), 'amenity', 'qr', v_disc.amount, v_disc.discount_id, v_disc.discount_amount, v_uid, v_ua,
		now() + make_interval(hours => (select payment_expiry_hours from public.gym_settings where id = 1))
	)
	returning * into v_pay;

	return v_pay;
end;
$$;

create function public.renew_membership(p_ump_id bigint, p_discount_code text default null)
returns public.payments
language plpgsql security definer
set search_path = ''
as $$
declare
	v_uid uuid := auth.uid();
	v_ump public.user_membership_packages;
	v_pkg public.membership_packages;
	v_disc record;
	v_start timestamptz;
	v_ren bigint;
	v_pay public.payments;
begin
	perform public.require_active_user();

	select * into v_ump from public.user_membership_packages
	where user_membership_package_id = p_ump_id and member_id = v_uid;
	if not found then perform public.fail('not_found', '22023'); end if;
	if v_ump.status not in ('active', 'expired') then perform public.fail('not_renewable', '22023'); end if;

	if exists (select 1 from public.membership_renewals where user_membership_package_id = p_ump_id and status = 'pending') then
		perform public.fail('renewal_pending', 'P0002');
	end if;

	select * into v_pkg from public.membership_packages where membership_package_id = v_ump.membership_package_id;
	select * into v_disc from public.apply_discount(v_uid, p_discount_code, v_pkg.price);

	v_start := greatest(v_ump.ends_at, now());
	insert into public.membership_renewals (user_membership_package_id, previous_ends_at, new_starts_at, new_ends_at)
	values (p_ump_id, v_ump.ends_at, v_start, v_start + make_interval(days => v_pkg.duration_days))
	returning renewal_id into v_ren;

	insert into public.payments (payment_code, kind, method, amount, discount_id, discount_amount, member_id, renewal_id, expires_at)
	values (
		public.generate_payment_code(), 'renewal', 'qr', v_disc.amount, v_disc.discount_id, v_disc.discount_amount, v_uid, v_ren,
		now() + make_interval(hours => (select payment_expiry_hours from public.gym_settings where id = 1))
	)
	returning * into v_pay;

	return v_pay;
end;
$$;

create function public.submit_payment_proof(p_payment_id bigint, p_reference_no text, p_proof_path text) returns void
language plpgsql security definer
set search_path = ''
as $$
declare
	v_pay public.payments;
begin
	perform public.require_active_user();
	if coalesce(btrim(p_reference_no), '') = '' or coalesce(btrim(p_proof_path), '') = '' then
		perform public.fail('bad_input', '22023');
	end if;

	select * into v_pay from public.payments where payment_id = p_payment_id and member_id = auth.uid();
	if not found then perform public.fail('not_found', '22023'); end if;
	if v_pay.status <> 'pending' or v_pay.method <> 'qr' or v_pay.expires_at <= now() then
		perform public.fail('already_processed', 'P0002');
	end if;

	update public.payments set reference_no = btrim(p_reference_no), proof_path = p_proof_path where payment_id = p_payment_id;

	perform public.notify_staff('payment', 'Payment waiting for review', v_pay.payment_code, jsonb_build_object('payment_id', p_payment_id));
end;
$$;

create function public.verify_payment(p_payment_id bigint, p_approve boolean, p_reason text default null) returns void
language plpgsql security definer
set search_path = ''
as $$
declare
	v_uid uuid := auth.uid();
	v_pay public.payments;
begin
	perform public.require_permission('payments:verify');

	select * into v_pay from public.payments where payment_id = p_payment_id for update;
	if not found then perform public.fail('not_found', '22023'); end if;
	if v_pay.status <> 'pending' then perform public.fail('already_processed', 'P0002'); end if;
	if v_pay.member_id = v_uid then perform public.fail('self_verify', '22023'); end if;

	if p_approve then
		update public.payments
		set status = 'verified', verified_by = v_uid, verified_at = now(), receipt_no = public.next_receipt_no()
		where payment_id = p_payment_id;
		perform public.activate_payment_target(p_payment_id);

		perform public.notify(v_pay.member_id, 'payment', 'Payment approved', v_pay.payment_code, jsonb_build_object('payment_id', p_payment_id));
		perform public.audit('payment.verify', 'payments', p_payment_id::text);
	else
		if coalesce(btrim(p_reason), '') = '' then perform public.fail('bad_input', '22023'); end if;
		update public.payments set status = 'rejected', rejected_reason = btrim(p_reason) where payment_id = p_payment_id;
		perform public.cancel_payment_target(p_payment_id);

		perform public.notify(v_pay.member_id, 'payment', 'Payment rejected', btrim(p_reason), jsonb_build_object('payment_id', p_payment_id));
		perform public.audit('payment.reject', 'payments', p_payment_id::text, jsonb_build_object('reason', btrim(p_reason)));
	end if;
end;
$$;

-- Staff record a payment made at the counter. It is verified straight away (plan 8.6).
-- p_item_id is the package id (membership), the membership id (renewal) or the amenity id (amenity).
create function public.record_counter_payment(
	p_kind public.payment_kind,
	p_method public.payment_method,
	p_member_id uuid default null,
	p_item_id bigint default null,
	p_walk_in_name text default null,
	p_reference_no text default null,
	p_discount_code text default null
) returns public.payments
language plpgsql security definer
set search_path = ''
as $$
declare
	v_uid uuid := auth.uid();
	v_price numeric;
	v_disc record;
	v_ump bigint;
	v_ren bigint;
	v_ua bigint;
	v_cur public.user_membership_packages;
	v_pkg public.membership_packages;
	v_start timestamptz;
	v_pay public.payments;
begin
	perform public.require_permission('payments:record');
	if p_method = 'qr' then perform public.fail('bad_input', '22023'); end if;
	if p_member_id = v_uid then perform public.fail('self_verify', '22023'); end if;
	if p_method in ('gcash', 'bank_transfer') and coalesce(btrim(p_reference_no), '') = '' then
		perform public.fail('bad_input', '22023');
	end if;

	if p_kind = 'walk_in' then
		if coalesce(btrim(p_walk_in_name), '') = '' then perform public.fail('bad_input', '22023'); end if;
		select walk_in_price into v_price from public.gym_settings where id = 1;
		select null::bigint as discount_id, 0::numeric as discount_amount, v_price as amount into v_disc;
	else
		if not exists (select 1 from public.members where user_id = p_member_id) then
			perform public.fail('not_found', '22023');
		end if;

		if p_kind = 'membership' then
			select * into v_pkg from public.membership_packages where membership_package_id = p_item_id and is_active;
			if not found then perform public.fail('not_found', '22023'); end if;
			if exists (select 1 from public.user_membership_packages where member_id = p_member_id and status in ('pending', 'active')) then
				perform public.fail('already_availed', 'P0002');
			end if;
			select * into v_disc from public.apply_discount(p_member_id, p_discount_code, v_pkg.price);
			insert into public.user_membership_packages (member_id, membership_package_id)
			values (p_member_id, v_pkg.membership_package_id)
			returning user_membership_package_id into v_ump;

		elsif p_kind = 'renewal' then
			select * into v_cur from public.user_membership_packages
			where user_membership_package_id = p_item_id and member_id = p_member_id;
			if not found then perform public.fail('not_found', '22023'); end if;
			if v_cur.status not in ('active', 'expired') then perform public.fail('not_renewable', '22023'); end if;
			if exists (select 1 from public.membership_renewals where user_membership_package_id = p_item_id and status = 'pending') then
				perform public.fail('renewal_pending', 'P0002');
			end if;
			select * into v_pkg from public.membership_packages where membership_package_id = v_cur.membership_package_id;
			select * into v_disc from public.apply_discount(p_member_id, p_discount_code, v_pkg.price);
			v_start := greatest(v_cur.ends_at, now());
			insert into public.membership_renewals (user_membership_package_id, previous_ends_at, new_starts_at, new_ends_at)
			values (p_item_id, v_cur.ends_at, v_start, v_start + make_interval(days => v_pkg.duration_days))
			returning renewal_id into v_ren;

		else -- amenity
			if not public.is_active_member(p_member_id) then perform public.fail('no_active_membership', '22023'); end if;
			select price into v_price from public.amenities where amenity_id = p_item_id and is_active;
			if not found then perform public.fail('not_found', '22023'); end if;
			if exists (select 1 from public.user_amenities where member_id = p_member_id and amenity_id = p_item_id and status in ('pending', 'active')) then
				perform public.fail('already_availed', 'P0002');
			end if;
			select * into v_disc from public.apply_discount(p_member_id, p_discount_code, v_price);
			insert into public.user_amenities (member_id, amenity_id)
			values (p_member_id, p_item_id)
			returning user_amenity_id into v_ua;
		end if;
	end if;

	insert into public.payments (
		payment_code, kind, method, status, amount, discount_id, discount_amount, member_id, walk_in_name,
		user_membership_package_id, renewal_id, user_amenity_id, reference_no,
		recorded_by, verified_by, verified_at, receipt_no
	)
	values (
		public.generate_payment_code(), p_kind, p_method, 'verified', v_disc.amount, v_disc.discount_id, v_disc.discount_amount,
		case when p_kind = 'walk_in' then null else p_member_id end,
		case when p_kind = 'walk_in' then btrim(p_walk_in_name) else null end,
		v_ump, v_ren, v_ua, nullif(btrim(p_reference_no), ''),
		v_uid, v_uid, now(), public.next_receipt_no()
	)
	returning * into v_pay;

	if p_kind = 'walk_in' then
		insert into public.gym_check_ins (payment_id, recorded_by) values (v_pay.payment_id, v_uid);
	else
		perform public.activate_payment_target(v_pay.payment_id);
		perform public.notify(p_member_id, 'payment', 'Payment received', v_pay.payment_code, jsonb_build_object('payment_id', v_pay.payment_id));
	end if;

	perform public.audit('payment.record', 'payments', v_pay.payment_id::text, jsonb_build_object('kind', p_kind, 'method', p_method));
	return v_pay;
end;
$$;

create function public.void_payment(p_payment_id bigint, p_reason text) returns void
language plpgsql security definer
set search_path = ''
as $$
declare
	v_uid uuid := auth.uid();
	v_pay public.payments;
	v_ren public.membership_renewals;
begin
	perform public.require_permission('payments:void');
	if coalesce(btrim(p_reason), '') = '' then perform public.fail('bad_input', '22023'); end if;

	select * into v_pay from public.payments where payment_id = p_payment_id for update;
	if not found then perform public.fail('not_found', '22023'); end if;
	if v_pay.status <> 'verified' then perform public.fail('already_processed', 'P0002'); end if;

	update public.payments
	set status = 'voided', voided_by = v_uid, voided_at = now(), void_reason = btrim(p_reason)
	where payment_id = p_payment_id;

	if v_pay.user_membership_package_id is not null then
		update public.user_membership_packages set status = 'cancelled'
		where user_membership_package_id = v_pay.user_membership_package_id;

	elsif v_pay.renewal_id is not null then
		select * into v_ren from public.membership_renewals where renewal_id = v_pay.renewal_id;
		update public.user_membership_packages
		set ends_at = v_ren.previous_ends_at,
		    status = case when v_ren.previous_ends_at > now() then 'active'::public.membership_status else 'expired'::public.membership_status end
		where user_membership_package_id = v_ren.user_membership_package_id;
		update public.membership_renewals set status = 'cancelled' where renewal_id = v_ren.renewal_id;

	elsif v_pay.user_amenity_id is not null then
		update public.user_amenities set status = 'cancelled' where user_amenity_id = v_pay.user_amenity_id;

	else -- walk-in: the check-in it created goes with it
		delete from public.gym_check_ins where payment_id = p_payment_id;
	end if;

	if v_pay.member_id is not null then
		perform public.notify(v_pay.member_id, 'payment', 'Payment voided', btrim(p_reason), jsonb_build_object('payment_id', p_payment_id));
	end if;
	perform public.audit('payment.void', 'payments', p_payment_id::text, jsonb_build_object('reason', btrim(p_reason)));
end;
$$;

-- Verified payments per day (Manila time), split by kind and method. Voided payments are not 'verified', so they drop out.
create function public.revenue_summary(p_from date, p_to date)
returns table (day date, kind public.payment_kind, method public.payment_method, payments bigint, total numeric)
language plpgsql stable security definer
set search_path = ''
as $$
begin
	perform public.require_permission('reports:revenue');
	return query
	select (p.verified_at at time zone 'Asia/Manila')::date, p.kind, p.method, count(*), sum(p.amount)
	from public.payments p
	where p.status = 'verified'
	  and (p.verified_at at time zone 'Asia/Manila')::date between p_from and p_to
	group by 1, 2, 3
	order by 1, 2, 3;
end;
$$;

create function public.daily_cash_summary(p_day date)
returns table (kind public.payment_kind, payments bigint, total numeric)
language plpgsql stable security definer
set search_path = ''
as $$
begin
	perform public.require_permission('reports:cash');
	return query
	select p.kind, count(*), sum(p.amount)
	from public.payments p
	where p.status = 'verified' and p.method = 'cash'
	  and (p.verified_at at time zone 'Asia/Manila')::date = p_day
	group by p.kind
	order by p.kind;
end;
$$;

-- =====================================================================
-- 7.2 Coaching
-- =====================================================================

create function public.hire_coach(p_coach_id uuid) returns bigint
language plpgsql security definer
set search_path = ''
as $$
declare
	v_uid uuid := auth.uid();
	v_id bigint;
begin
	perform public.require_active_user();
	if not public.is_active_member(v_uid) then perform public.fail('no_active_membership', '22023'); end if;

	if not exists (
		select 1 from public.coaches c join public.profiles p on p.id = c.user_id
		where c.user_id = p_coach_id and c.is_available and p.is_active
	) then
		perform public.fail('not_found', '22023');
	end if;

	insert into public.coach_hires (member_id, coach_id) values (v_uid, p_coach_id)
	returning coach_hire_id into v_id;

	perform public.notify(p_coach_id, 'system', 'New trainee', (select full_name from public.profiles where id = v_uid), jsonb_build_object('member_id', v_uid));
	return v_id;
end;
$$;

create function public.end_hire(p_hire_id bigint) returns void
language plpgsql security definer
set search_path = ''
as $$
declare
	v_uid uuid := auth.uid();
	v_hire public.coach_hires;
begin
	perform public.require_active_user();

	select * into v_hire from public.coach_hires where coach_hire_id = p_hire_id and (member_id = v_uid or coach_id = v_uid) for update;
	if not found then perform public.fail('not_found', '22023'); end if;
	if v_hire.status <> 'active' then perform public.fail('already_processed', 'P0002'); end if;

	update public.coach_hires set status = 'ended', ended_at = now() where coach_hire_id = p_hire_id;

	update public.training_sessions set status = 'cancelled'
	where coach_id = v_hire.coach_id and member_id = v_hire.member_id and status = 'scheduled' and scheduled_start > now();

	update public.time_requests set status = 'cancelled'
	where coach_id = v_hire.coach_id and member_id = v_hire.member_id and status = 'pending';

	perform public.notify(case when v_uid = v_hire.coach_id then v_hire.member_id else v_hire.coach_id end,
	                      'system', 'Coaching ended', null, jsonb_build_object('coach_hire_id', p_hire_id));
end;
$$;

-- A member asks a coach they have hired for a time, or a coach offers an open slot (no member yet).
create function public.request_time(p_coach_id uuid, p_start timestamptz, p_end timestamptz, p_message text default null)
returns bigint
language plpgsql security definer
set search_path = ''
as $$
declare
	v_uid uuid := auth.uid();
	v_id bigint;
begin
	perform public.require_active_user();
	if p_end <= p_start then perform public.fail('end must be after start', '22023'); end if;

	if v_uid = p_coach_id then
		if not exists (select 1 from public.coaches where user_id = v_uid) then perform public.fail('forbidden', '42501'); end if;
		insert into public.time_requests (coach_id, requested_by, requested_start, requested_end, message)
		values (v_uid, 'coach', p_start, p_end, nullif(btrim(p_message), ''))
		returning time_request_id into v_id;

		insert into public.user_notifications (user_id, type, title, reference)
		select member_id, 'time_request', 'Your coach offered an open slot', jsonb_build_object('time_request_id', v_id)
		from public.coach_hires where coach_id = v_uid and status = 'active';
	else
		if not public.is_active_member(v_uid) then perform public.fail('no_active_membership', '22023'); end if;
		if not public.coaches_member(p_coach_id, v_uid) then perform public.fail('no_active_hire', '22023'); end if;
		insert into public.time_requests (coach_id, member_id, requested_by, requested_start, requested_end, message)
		values (p_coach_id, v_uid, 'member', p_start, p_end, nullif(btrim(p_message), ''))
		returning time_request_id into v_id;

		perform public.notify(p_coach_id, 'time_request', 'New time request', null, jsonb_build_object('time_request_id', v_id));
	end if;

	return v_id;
end;
$$;

create function public.request_reschedule(p_session_id bigint, p_start timestamptz, p_end timestamptz, p_message text default null)
returns bigint
language plpgsql security definer
set search_path = ''
as $$
declare
	v_uid uuid := auth.uid();
	v_s public.training_sessions;
	v_by_coach boolean;
	v_id bigint;
begin
	perform public.require_active_user();
	if p_end <= p_start then perform public.fail('end must be after start', '22023'); end if;

	select * into v_s from public.training_sessions
	where training_session_id = p_session_id and (coach_id = v_uid or member_id = v_uid);
	if not found then perform public.fail('not_found', '22023'); end if;
	if v_s.status <> 'scheduled' or v_s.scheduled_start <= now()
	   or exists (select 1 from public.time_requests where training_session_id = p_session_id and status = 'pending') then
		perform public.fail('already_processed', 'P0002');
	end if;

	v_by_coach := (v_uid = v_s.coach_id);
	insert into public.time_requests (coach_id, member_id, training_session_id, requested_by, requested_start, requested_end, message)
	values (v_s.coach_id, v_s.member_id, p_session_id, case when v_by_coach then 'coach'::public.requested_by_role else 'member'::public.requested_by_role end,
	        p_start, p_end, nullif(btrim(p_message), ''))
	returning time_request_id into v_id;

	perform public.notify(case when v_by_coach then v_s.member_id else v_s.coach_id end,
	                      'time_request', 'Reschedule requested', null, jsonb_build_object('time_request_id', v_id));
	return v_id;
end;
$$;

-- Approve or reject a time request. Overlaps are refused by the exclusion constraints (23P01).
create function public.respond_time_request(p_request_id bigint, p_approve boolean) returns void
language plpgsql security definer
set search_path = ''
as $$
declare
	v_uid uuid := auth.uid();
	v_r public.time_requests;
	v_member uuid;
begin
	perform public.require_active_user();

	select * into v_r from public.time_requests where time_request_id = p_request_id for update;
	if not found then perform public.fail('not_found', '22023'); end if;
	if v_r.status <> 'pending' then perform public.fail('already_processed', 'P0002'); end if;

	if v_r.member_id is null then
		-- an open slot: a trainee of that coach claims it, which counts as approving
		if not (public.is_active_member(v_uid) and public.coaches_member(v_r.coach_id, v_uid)) then
			perform public.fail('forbidden', '42501');
		end if;
		if not p_approve then perform public.fail('bad_input', '22023'); end if;
		v_member := v_uid;
	elsif v_r.requested_by = 'member' then
		if v_uid <> v_r.coach_id then perform public.fail('forbidden', '42501'); end if;
		v_member := v_r.member_id;
	else
		if v_uid <> v_r.member_id then perform public.fail('forbidden', '42501'); end if;
		v_member := v_r.member_id;
	end if;

	if p_approve then
		if v_r.training_session_id is null and not public.is_active_member(v_member) then
			perform public.fail('no_active_membership', '22023');
		end if;

		if v_r.training_session_id is not null then
			update public.training_sessions
			set scheduled_start = v_r.requested_start, scheduled_end = v_r.requested_end
			where training_session_id = v_r.training_session_id and status = 'scheduled';
			if not found then perform public.fail('already_processed', 'P0002'); end if;
		else
			insert into public.training_sessions (coach_id, member_id, title, scheduled_start, scheduled_end)
			values (v_r.coach_id, v_member, 'Training session', v_r.requested_start, v_r.requested_end);
		end if;

		update public.time_requests set status = 'approved', member_id = v_member, responded_at = now() where time_request_id = p_request_id;
	else
		update public.time_requests set status = 'rejected', responded_at = now() where time_request_id = p_request_id;
	end if;

	perform public.notify(case when v_uid = v_r.coach_id then v_member else v_r.coach_id end,
	                      'time_request', case when p_approve then 'Time request approved' else 'Time request declined' end,
	                      null, jsonb_build_object('time_request_id', p_request_id));
end;
$$;

create function public.cancel_session(p_session_id bigint, p_reason text default null) returns void
language plpgsql security definer
set search_path = ''
as $$
declare
	v_uid uuid := auth.uid();
	v_s public.training_sessions;
begin
	perform public.require_active_user();

	select * into v_s from public.training_sessions
	where training_session_id = p_session_id and (coach_id = v_uid or member_id = v_uid) for update;
	if not found then perform public.fail('not_found', '22023'); end if;
	if v_s.status <> 'scheduled' then perform public.fail('already_processed', 'P0002'); end if;

	update public.training_sessions
	set status = 'cancelled', notes = coalesce(nullif(btrim(p_reason), ''), notes)
	where training_session_id = p_session_id;

	update public.time_requests set status = 'cancelled' where training_session_id = p_session_id and status = 'pending';

	perform public.notify(case when v_uid = v_s.coach_id then v_s.member_id else v_s.coach_id end,
	                      'system', 'A training session was cancelled', nullif(btrim(p_reason), ''), jsonb_build_object('training_session_id', p_session_id));
end;
$$;

create function public.record_session_attendance(p_session_id bigint, p_status public.attendance_status, p_notes text default null)
returns void
language plpgsql security definer
set search_path = ''
as $$
declare
	v_uid uuid := auth.uid();
	v_s public.training_sessions;
begin
	perform public.require_active_user();

	select * into v_s from public.training_sessions where training_session_id = p_session_id and coach_id = v_uid for update;
	if not found then perform public.fail('forbidden', '42501'); end if;
	if v_s.status = 'cancelled' then perform public.fail('already_processed', 'P0002'); end if;

	insert into public.session_attendance (training_session_id, status, checked_in_at, recorded_by, notes)
	values (p_session_id, p_status, case when p_status in ('present', 'late') then now() end, v_uid, nullif(btrim(p_notes), ''))
	on conflict (training_session_id) do update
	set status = excluded.status, checked_in_at = excluded.checked_in_at, recorded_by = excluded.recorded_by, notes = excluded.notes;

	update public.training_sessions
	set status = case when p_status = 'absent' then 'no_show'::public.session_status else 'completed'::public.session_status end
	where training_session_id = p_session_id;

	perform public.notify(v_s.member_id, 'attendance', 'Attendance recorded', p_status::text, jsonb_build_object('training_session_id', p_session_id));
end;
$$;

-- =====================================================================
-- 7.3 Front desk and accounts
-- =====================================================================

create function public.record_check_in(p_member_id uuid) returns bigint
language plpgsql security definer
set search_path = ''
as $$
declare
	v_id bigint;
begin
	perform public.require_permission('check_ins:record');
	if not exists (select 1 from public.members where user_id = p_member_id) then perform public.fail('not_found', '22023'); end if;
	if not public.is_active_member(p_member_id) then perform public.fail('no_active_membership', '22023'); end if;

	insert into public.gym_check_ins (member_id, recorded_by) values (p_member_id, auth.uid())
	returning check_in_id into v_id;
	return v_id;
end;
$$;

create function public.verify_amenity_usage(p_usage_id bigint, p_approve boolean) returns void
language plpgsql security definer
set search_path = ''
as $$
declare
	v_u public.amenity_usages;
	v_member uuid;
begin
	perform public.require_permission('amenity_usages:verify');

	select * into v_u from public.amenity_usages where amenity_usage_id = p_usage_id for update;
	if not found then perform public.fail('not_found', '22023'); end if;
	if v_u.status <> 'pending' then perform public.fail('already_processed', 'P0002'); end if;

	update public.amenity_usages
	set status = case when p_approve then 'verified'::public.amenity_usage_status else 'rejected'::public.amenity_usage_status end,
	    verified_by = auth.uid(), verified_at = now()
	where amenity_usage_id = p_usage_id;

	select member_id into v_member from public.user_amenities where user_amenity_id = v_u.user_amenity_id;
	perform public.notify(v_member, 'system', case when p_approve then 'Amenity visit confirmed' else 'Amenity visit rejected' end,
	                      null, jsonb_build_object('amenity_usage_id', p_usage_id));
end;
$$;

create function public.assign_role(p_user uuid, p_role text) returns void
language plpgsql security definer
set search_path = ''
as $$
declare
	v_role smallint;
begin
	perform public.require_permission('roles:manage');

	select role_id into v_role from public.roles where name = p_role;
	if not found then perform public.fail('bad_input', '22023'); end if;
	if not exists (select 1 from public.profiles where id = p_user) then perform public.fail('not_found', '22023'); end if;

	insert into public.user_roles (user_id, role_id, assigned_by) values (p_user, v_role, auth.uid()) on conflict do nothing;
	if p_role = 'coach' then
		insert into public.coaches (user_id) values (p_user) on conflict do nothing;
	elsif p_role = 'member' then
		insert into public.members (user_id) values (p_user) on conflict do nothing;
	end if;

	perform public.audit('role.assign', 'user_roles', p_user::text, jsonb_build_object('role', p_role));
end;
$$;

create function public.revoke_role(p_user uuid, p_role text) returns void
language plpgsql security definer
set search_path = ''
as $$
begin
	perform public.require_permission('roles:manage');
	if p_user = auth.uid() and p_role = 'admin' then perform public.fail('forbidden', '42501'); end if;

	delete from public.user_roles
	where user_id = p_user and role_id = (select role_id from public.roles where name = p_role);

	perform public.audit('role.revoke', 'user_roles', p_user::text, jsonb_build_object('role', p_role));
end;
$$;

create function public.set_user_active(p_user uuid, p_active boolean) returns void
language plpgsql security definer
set search_path = ''
as $$
begin
	perform public.require_permission('users:manage');
	if p_user = auth.uid() then perform public.fail('forbidden', '42501'); end if;

	update public.profiles set is_active = p_active where id = p_user;
	if not found then perform public.fail('not_found', '22023'); end if;

	perform public.audit(case when p_active then 'user.activate' else 'user.deactivate' end, 'profiles', p_user::text);
end;
$$;

-- Called by the app right after auth.updateUser({ password }) succeeds (plan 4.5).
create function public.mark_password_changed() returns void
language sql security definer
set search_path = ''
as $$
	update public.profiles set must_change_password = false where id = auth.uid();
$$;

-- =====================================================================
-- Notification triggers: a chat message and a new shift
-- =====================================================================

create function public.notify_new_message() returns trigger
language plpgsql security definer
set search_path = ''
as $$
begin
	perform public.notify(
		case when new.sender_id = new.coach_id then new.member_id else new.coach_id end,
		'message', 'New message', null,
		jsonb_build_object('coach_id', new.coach_id, 'member_id', new.member_id)
	);
	return new;
end;
$$;

create trigger messages_notify after insert on public.messages
	for each row execute function public.notify_new_message();

create function public.notify_new_shift() returns trigger
language plpgsql security definer
set search_path = ''
as $$
begin
	insert into public.user_notifications (user_id, type, title, reference)
	select member_id, 'system', 'Your coach posted a shift', jsonb_build_object('shift_post_id', new.shift_post_id)
	from public.coach_hires where coach_id = new.coach_id and status = 'active';
	return new;
end;
$$;

create trigger shift_posts_notify after insert on public.shift_posts
	for each row execute function public.notify_new_shift();
