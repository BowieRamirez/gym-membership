-- Phase 7 of the 2026-11-12 plan (owner decisions G16, G17, G18, 2026-11-14): gym closing days.
-- G16: Admin and System admin close the gym for a whole day (Manila) with a reason, today or later; it can be reopened up to the day.
-- G17: nothing can be set up on a closed day. One trigger on every table where a time is set up (bookings, coach shifts, training
--      sessions, session requests and proposals, membership check-ins) refuses `day_closed`; it looks only at a time that is inserted
--      or changed, so cancelling a row on a closed day still works. Memberships keep counting down (no days back).
-- G18: closing cancels what is set up that day and not yet over; paid bookings go to Refunds (approved, as when the gym cancels);
--      an announcement goes to everyone, and each person affected gets a personal note. Reopening announces it and restores nothing.

create table public.gym_closures (
  closed_on date primary key,
  reason text not null check (char_length(btrim(reason)) between 1 and 200),
  closed_by uuid not null references public.profiles(id),
  closed_at timestamptz not null default now()
);
alter table public.gym_closures enable row level security;
create policy gym_closures_read on public.gym_closures for select to authenticated using (public.is_active_user());
revoke all on public.gym_closures from public, anon, authenticated;
grant select on public.gym_closures to authenticated;

create function public.day_is_closed(p_at timestamptz) returns boolean
language sql stable security definer set search_path = '' as $$
  select exists (select 1 from public.gym_closures where closed_on = (p_at at time zone 'Asia/Manila')::date);
$$;
revoke all on function public.day_is_closed(timestamptz) from public, anon, authenticated;

-- the arguments name the time columns to check
create function public.refuse_closed_day() returns trigger
language plpgsql security definer set search_path = '' as $$
declare v_col text; v_new timestamptz;
begin
  foreach v_col in array tg_argv loop
    v_new := (to_jsonb(new) ->> v_col)::timestamptz;
    if v_new is not null and (tg_op = 'INSERT' or v_new is distinct from (to_jsonb(old) ->> v_col)::timestamptz)
       and public.day_is_closed(v_new) then
      perform public.fail('day_closed', '22023');
    end if;
  end loop;
  return new;
end $$;
revoke all on function public.refuse_closed_day() from public, anon, authenticated;

create trigger zz_day_closed before insert or update of starts_at on public.reservations
  for each row execute function public.refuse_closed_day('starts_at');
create trigger zz_day_closed before insert or update of starts_at on public.shift_posts
  for each row execute function public.refuse_closed_day('starts_at');
create trigger zz_day_closed before insert or update of scheduled_start on public.training_sessions
  for each row execute function public.refuse_closed_day('scheduled_start');
create trigger zz_day_closed before insert or update of requested_start, proposed_start on public.time_requests
  for each row execute function public.refuse_closed_day('requested_start', 'proposed_start');
create trigger zz_day_closed before insert on public.gym_check_ins
  for each row execute function public.refuse_closed_day('checked_in_at');

create function public.require_gym_admin() returns void
language plpgsql stable security definer set search_path = '' as $$
begin
  perform public.require_active_user();
  if not (public.has_role('admin') or public.has_role('subadmin')) then perform public.fail('forbidden', '42501'); end if;
end $$;
revoke all on function public.require_gym_admin() from public, anon, authenticated;

create function public.require_open_or_future(p_date date) returns void
language plpgsql stable set search_path = '' as $$
begin
  if p_date is null then perform public.fail('bad_input', '22023'); end if;
  if p_date < (now() at time zone 'Asia/Manila')::date then perform public.fail('day_past', '22023'); end if;
end $$;
revoke all on function public.require_open_or_future(date) from public, anon, authenticated;

-- what closing a day would cancel: set up that day and not yet over
create function public.closure_preview(p_date date) returns jsonb
language plpgsql stable security definer set search_path = '' as $$
begin
  perform public.require_gym_admin();
  perform public.require_open_or_future(p_date);
  return jsonb_build_object(
    'bookings', (select count(*) from public.reservations r where r.status in ('confirmed', 'awaiting_payment')
                   and (r.starts_at at time zone 'Asia/Manila')::date = p_date and r.ends_at > now()),
    'paid', (select count(*) from public.reservations r left join public.payments p on p.payment_id = r.payment_id
               where r.status = 'confirmed' and (p.status = 'verified' or r.balance_payment_id is not null)
                 and (r.starts_at at time zone 'Asia/Manila')::date = p_date and r.ends_at > now()),
    'sessions', (select count(*) from public.training_sessions t where t.status = 'scheduled'
                   and (t.scheduled_start at time zone 'Asia/Manila')::date = p_date and t.scheduled_end > now()),
    'shifts', (select count(*) from public.shift_posts s where s.status = 'scheduled'
                 and (s.starts_at at time zone 'Asia/Manila')::date = p_date and s.ends_at > now()),
    'requests', (select count(*) from public.time_requests q where q.status in ('pending', 'proposed')
                   and (case when q.status = 'proposed' then q.proposed_start else q.requested_start end at time zone 'Asia/Manila')::date = p_date));
end $$;
revoke all on function public.closure_preview(date) from public, anon;
grant execute on function public.closure_preview(date) to authenticated;

create function public.close_gym_day(p_date date, p_reason text) returns void
language plpgsql security definer set search_path = '' as $$
declare
  v_uid uuid := auth.uid(); v_reason text := btrim(coalesce(p_reason, '')); v_why text;
  v public.reservations; v_pay public.payments; v_bal numeric; v_req bigint; s record;
begin
  perform public.require_gym_admin();
  perform public.require_open_or_future(p_date);
  if char_length(v_reason) not between 1 and 200 then perform public.fail('bad_reason', '22023'); end if;
  insert into public.gym_closures (closed_on, reason, closed_by) values (p_date, v_reason, v_uid) on conflict do nothing;
  if not found then perform public.fail('already_processed', 'P0002'); end if;
  v_why := 'Gym closed: ' || v_reason;

  -- bookings: as when the gym cancels (cancel_reservation, 0064), but also once started or with the balance paid
  for v in select * from public.reservations r where r.status in ('confirmed', 'awaiting_payment')
             and (r.starts_at at time zone 'Asia/Manila')::date = p_date and r.ends_at > now() for update loop
    update public.reservations set status = 'cancelled', cancelled_at = now(), cancelled_by = v_uid, cancel_reason = v_why
      where reservation_id = v.reservation_id;
    v_pay := null; v_bal := 0;
    if v.payment_id is not null then select * into v_pay from public.payments where payment_id = v.payment_id for update; end if;
    if v.balance_payment_id is not null then select amount into v_bal from public.payments where payment_id = v.balance_payment_id; end if;
    if v.status = 'awaiting_payment' then
      update public.payments set status = 'rejected', rejected_reason = 'Booking cancelled' where payment_id = v.payment_id and status = 'pending';
      perform public.cancel_payment_target(v.payment_id);
    elsif v_pay.status = 'verified' or v.balance_payment_id is not null then
      insert into public.refund_requests (kind, reservation_id, payment_id, member_id, amount, reason, status, decided_by, decided_at, decision_reason)
      values ('cancel', v.reservation_id, case when v_pay.status = 'verified' then v_pay.payment_id else v.balance_payment_id end, v.member_id,
              case when v_pay.status = 'verified' then v_pay.amount else 0 end + v_bal, v_why, 'approved', v_uid, now(), 'Cancelled by the gym: refunded')
      returning refund_request_id into v_req;
      perform public.notify_staff('payment', 'Booking refund to repay', v_why, jsonb_build_object('refund_request_id', v_req));
    elsif v.user_amenity_id is not null then
      update public.user_amenities set status = 'active' where user_amenity_id = v.user_amenity_id and is_session;
    end if;
    perform public.notify(v.member_id, 'amenity', 'Booking cancelled', v_why, jsonb_build_object('reservation_id', v.reservation_id));
  end loop;

  for s in select * from public.training_sessions t where t.status = 'scheduled'
             and (t.scheduled_start at time zone 'Asia/Manila')::date = p_date and t.scheduled_end > now() for update loop
    update public.training_sessions set status = 'cancelled', notes = v_why where training_session_id = s.training_session_id;
    update public.time_requests set status = 'cancelled' where training_session_id = s.training_session_id and status = 'pending';
    perform public.notify(s.member_id, 'system', 'A training session was cancelled', v_why, jsonb_build_object('training_session_id', s.training_session_id));
    perform public.notify(s.coach_id, 'system', 'A training session was cancelled', v_why, jsonb_build_object('training_session_id', s.training_session_id));
  end loop;

  for s in select * from public.shift_posts p where p.status = 'scheduled'
             and (p.starts_at at time zone 'Asia/Manila')::date = p_date and p.ends_at > now() for update loop
    update public.shift_posts set status = 'cancelled' where shift_post_id = s.shift_post_id;
    perform public.notify(s.coach_id, 'system', 'Shift cancelled', v_why, jsonb_build_object('shift_post_id', s.shift_post_id));
  end loop;

  for s in select * from public.time_requests q where q.status in ('pending', 'proposed')
             and (case when q.status = 'proposed' then q.proposed_start else q.requested_start end at time zone 'Asia/Manila')::date = p_date for update loop
    update public.time_requests set status = 'rejected', decline_reason = v_why, responded_at = now() where time_request_id = s.time_request_id;
    perform public.notify(s.member_id, 'time_request', 'Session request declined', v_why, jsonb_build_object('time_request_id', s.time_request_id));
  end loop;

  perform public.post_announcement(format('The gym is closed on %s: %s.', to_char(p_date, 'FMDay, FMDD Mon'), rtrim(v_reason, '.')), 'everyone');
  perform public.audit('gym.close', 'gym_closures', p_date::text, jsonb_build_object('reason', v_reason));
end $$;
revoke all on function public.close_gym_day(date, text) from public, anon;
grant execute on function public.close_gym_day(date, text) to authenticated;

create function public.reopen_gym_day(p_date date) returns void
language plpgsql security definer set search_path = '' as $$
begin
  perform public.require_gym_admin();
  perform public.require_open_or_future(p_date);
  delete from public.gym_closures where closed_on = p_date;
  if not found then perform public.fail('not_found', '22023'); end if;
  perform public.post_announcement(format('The gym is open again on %s.', to_char(p_date, 'FMDay, FMDD Mon')), 'everyone');
  perform public.audit('gym.reopen', 'gym_closures', p_date::text, null);
end $$;
revoke all on function public.reopen_gym_day(date) from public, anon;
grant execute on function public.reopen_gym_day(date) to authenticated;
