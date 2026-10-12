-- 0003: role/permission helpers (plan 5.1) and internal helpers (plan 7.4)
--
-- Error convention (plan 7): the message is a token the app's ErrorMapper already
-- understands, the SQLSTATE says which kind of failure it is:
--   42501 not allowed   |   22023 bad input   |   P0002 wrong state / duplicate (23505, 23P01 come from constraints)

create sequence public.receipt_no_seq;

-- ---------------------------------------------------------------------
-- Raising errors
-- ---------------------------------------------------------------------

create function public.fail(p_message text, p_sqlstate text default '22023') returns void
language plpgsql
set search_path = ''
as $$
begin
	raise exception '%', p_message using errcode = p_sqlstate;
end;
$$;

-- ---------------------------------------------------------------------
-- Who is calling (5.1). stable + security definer so RLS policies can use them
-- ---------------------------------------------------------------------

create function public.has_role(p_role text) returns boolean
language sql stable security definer
set search_path = ''
as $$
	select exists (
		select 1
		from public.user_roles ur
		join public.roles r on r.role_id = ur.role_id
		where ur.user_id = auth.uid() and r.name = p_role
	);
$$;

create function public.has_permission(p_permission text) returns boolean
language sql stable security definer
set search_path = ''
as $$
	select exists (
		select 1
		from public.user_roles ur
		join public.role_permissions rp on rp.role_id = ur.role_id
		join public.permissions p on p.permission_id = rp.permission_id
		where ur.user_id = auth.uid() and p.resource || ':' || p.action = p_permission
	);
$$;

create function public.is_staff() returns boolean
language sql stable security definer
set search_path = ''
as $$
	select public.has_role('admin') or public.has_role('employee');
$$;

create function public.is_active_user() returns boolean
language sql stable security definer
set search_path = ''
as $$
	select coalesce((select is_active from public.profiles where id = auth.uid()), false);
$$;

-- "Not a guest": the user has an active membership that has not run out
create function public.is_active_member(p_user uuid default auth.uid()) returns boolean
language sql stable security definer
set search_path = ''
as $$
	select exists (
		select 1
		from public.user_membership_packages
		where member_id = p_user and status = 'active' and ends_at > now()
	);
$$;

create function public.coaches_member(p_coach uuid, p_member uuid) returns boolean
language sql stable security definer
set search_path = ''
as $$
	select exists (
		select 1 from public.coach_hires
		where coach_id = p_coach and member_id = p_member and status = 'active'
	);
$$;

-- Guards used at the top of the server-side functions
create function public.require_active_user() returns void
language plpgsql stable security definer
set search_path = ''
as $$
begin
	if not public.is_active_user() then
		perform public.fail('forbidden', '42501');
	end if;
end;
$$;

create function public.require_permission(p_permission text) returns void
language plpgsql stable security definer
set search_path = ''
as $$
begin
	if not (public.is_active_user() and public.has_permission(p_permission)) then
		perform public.fail('forbidden', '42501');
	end if;
end;
$$;

-- ---------------------------------------------------------------------
-- Internal helpers (7.4). Not callable from the app (revoked in 0005)
-- ---------------------------------------------------------------------

create function public.notify(p_user uuid, p_type public.notification_type, p_title text, p_body text default null, p_reference jsonb default null)
returns void
language sql security definer
set search_path = ''
as $$
	insert into public.user_notifications (user_id, type, title, body, reference)
	values (p_user, p_type, p_title, p_body, p_reference);
$$;

-- every active staff member (admin or employee)
create function public.notify_staff(p_type public.notification_type, p_title text, p_body text default null, p_reference jsonb default null)
returns void
language sql security definer
set search_path = ''
as $$
	insert into public.user_notifications (user_id, type, title, body, reference)
	select distinct ur.user_id, p_type, p_title, p_body, p_reference
	from public.user_roles ur
	join public.roles r on r.role_id = ur.role_id
	join public.profiles pr on pr.id = ur.user_id and pr.is_active
	where r.name in ('admin', 'employee');
$$;

create function public.audit(p_action text, p_entity text, p_entity_id text default null, p_details jsonb default null)
returns void
language sql security definer
set search_path = ''
as $$
	insert into public.audit_log (actor_id, action, entity, entity_id, details)
	values (auth.uid(), p_action, p_entity, p_entity_id, p_details);
$$;

-- GYM- plus 5 characters; the alphabet leaves out 0/O, 1/I/L so a code can be read aloud
create function public.generate_payment_code() returns text
language plpgsql
set search_path = ''
as $$
declare
	chars constant text := 'ABCDEFGHJKMNPQRSTUVWXYZ23456789';
	v_code text;
	i int;
begin
	loop
		v_code := 'GYM-';
		for i in 1..5 loop
			v_code := v_code || substr(chars, 1 + floor(random() * length(chars))::int, 1);
		end loop;
		exit when not exists (select 1 from public.payments where payment_code = v_code);
	end loop;
	return v_code;
end;
$$;

create function public.next_receipt_no() returns text
language sql security definer
set search_path = ''
as $$
	select concat(
		receipt_prefix, '-',
		extract(year from now() at time zone 'Asia/Manila')::int, '-',
		lpad(nextval('public.receipt_no_seq')::text, 6, '0')
	)
	from public.gym_settings
	where id = 1;
$$;

-- Plan 8.3. No code: full price. A bad, expired or already-used code is an error, never a silent full price.
create function public.apply_discount(p_member uuid, p_code text, p_price numeric)
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
	  and d.is_active and d.starts_at <= now() and d.expires_at > now();

	if not found or exists (
		select 1 from public.payments p
		where p.member_id = p_member and p.discount_id = v_disc.discount_id and p.status in ('pending', 'verified')
	) then
		perform public.fail('invalid_code', '22023');
	end if;

	v_cut := round(p_price * v_disc.percent / 100.0, 2);
	return query select v_disc.discount_id, v_cut, p_price - v_cut;
end;
$$;
