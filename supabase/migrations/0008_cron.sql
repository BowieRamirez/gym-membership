-- 0008: scheduled jobs (plan 10). pg_cron runs in UTC; Manila is UTC+8.

-- Unpaid QR payments past their deadline expire, and what they would have switched on is cancelled.
create function public.expire_payments() returns int
language plpgsql security definer
set search_path = ''
as $$
declare
	v_pay record;
	v_count int := 0;
begin
	for v_pay in
		select payment_id, member_id, payment_code from public.payments
		where status = 'pending' and method = 'qr' and expires_at <= now()
		for update
	loop
		update public.payments set status = 'expired' where payment_id = v_pay.payment_id;
		perform public.cancel_payment_target(v_pay.payment_id);
		perform public.notify(v_pay.member_id, 'payment', 'Payment expired', v_pay.payment_code,
		                      jsonb_build_object('payment_id', v_pay.payment_id));
		v_count := v_count + 1;
	end loop;
	return v_count;
end;
$$;

-- Memberships and amenities past their end become expired, so the member is a guest again.
create function public.expire_memberships() returns int
language plpgsql security definer
set search_path = ''
as $$
declare
	v_row record;
	v_count int := 0;
begin
	for v_row in
		update public.user_membership_packages set status = 'expired'
		where status = 'active' and ends_at <= now()
		returning member_id, user_membership_package_id
	loop
		perform public.notify(v_row.member_id, 'membership', 'Your membership has expired', null,
		                      jsonb_build_object('membership_id', v_row.user_membership_package_id));
		v_count := v_count + 1;
	end loop;

	update public.user_amenities set status = 'expired'
	where status = 'active' and expires_at is not null and expires_at <= now();

	return v_count;
end;
$$;

-- Tell members their membership ends in 3 days (Manila calendar days), once per membership.
create function public.send_expiry_reminders() returns int
language plpgsql security definer
set search_path = ''
as $$
declare
	v_row record;
	v_count int := 0;
begin
	for v_row in
		select user_membership_package_id, member_id from public.user_membership_packages u
		where u.status = 'active'
		  and (u.ends_at at time zone 'Asia/Manila')::date = ((now() at time zone 'Asia/Manila')::date + 3)
		  and not exists (
			select 1 from public.user_notifications n
			where n.user_id = u.member_id and n.type = 'membership'
			  and n.reference @> jsonb_build_object('membership_id', u.user_membership_package_id, 'reminder', '3d')
		  )
	loop
		perform public.notify(v_row.member_id, 'membership', 'Your membership ends in 3 days', null,
		                      jsonb_build_object('membership_id', v_row.user_membership_package_id, 'reminder', '3d'));
		v_count := v_count + 1;
	end loop;
	return v_count;
end;
$$;

-- owner only: the jobs below run as the database owner, the app can't call these
revoke all on function public.expire_payments(), public.expire_memberships(), public.send_expiry_reminders()
	from public, anon, authenticated;

select cron.schedule('expire-payments',        '*/15 * * * *', $$select public.expire_payments()$$);
select cron.schedule('expire-memberships',     '5 16 * * *',   $$select public.expire_memberships()$$);
select cron.schedule('send-expiry-reminders',  '0 0 * * *',    $$select public.send_expiry_reminders()$$);
