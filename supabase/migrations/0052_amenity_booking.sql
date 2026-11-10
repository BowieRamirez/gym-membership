-- Phase 5 of the 2026-11-09 plan (owner decisions F12, F13, F14, 2026-11-10).
--
-- F12 (reverses D11's buy-a-session-first): a member picks an amenity, a start and a length (30 minutes to 2 hours), and the booking
-- holds the slot as 'awaiting_payment' with a payment for one session (QR/GCash proof, or cash at the desk). The payment's expires_at
-- is the hold: the gym's payment expiry hours or 1 hour before the start, whichever comes first. Holds take their slot and cooldown in
-- the same exclusion constraints as confirmed bookings. One trigger on user_amenities settles the hold whichever way the payment goes
-- (verified, rejected, expired, withdrawn), so every payment path is covered. A member with a pass or an unused session still books
-- straight away (book_slots), until it runs out.
-- F13: cancelling a paid booking 1 hour or more before it starts gives an approved full refund at once; within the hour the member
-- can't cancel. Up to 24 hours after a paid booking ends the member can say "I didn't use it"; the front desk approves or declines it
-- with a reason. Repayment is recorded as for memberships (record_membership_refund_paid).
-- F14: coaches say which amenities they run. Where an amenity has assigned coaches, one of them must be in the gym at the time of use;
-- amenities with no one assigned keep D11's "any checked-in coach".

-- ---------- reservations: the hold ----------
alter table public.reservations drop constraint reservations_status_check;
alter table public.reservations add constraint reservations_status_check check (status in ('confirmed', 'cancelled', 'awaiting_payment'));
alter table public.reservations add column payment_id bigint unique references public.payments;
alter table public.reservations drop constraint reservations_amenity_cooldown, drop constraint reservations_member_busy, drop constraint reservations_coach_busy;
alter table public.reservations
  add constraint reservations_amenity_cooldown exclude using gist (
    amenity_id with =, tstzrange(starts_at, public.plus_cooldown(ends_at)) with &&) where (status in ('confirmed', 'awaiting_payment')),
  add constraint reservations_member_busy exclude using gist (
    member_id with =, tstzrange(starts_at, ends_at) with &&) where (status in ('confirmed', 'awaiting_payment')),
  add constraint reservations_coach_busy exclude using gist (
    coach_id with =, tstzrange(starts_at, ends_at) with &&) where (status in ('confirmed', 'awaiting_payment') and coach_id is not null);

-- "Pay at the desk": the member's booking makes a pending cash payment with a deadline; the desk verifying it takes the cash into
-- their shift (0050). Until now only the counter could make a cash payment.
alter table public.payments drop constraint payments_counter_recorded;
alter table public.payments add constraint payments_counter_recorded check (method = 'qr' or recorded_by is not null or expires_at is not null);

-- ---------- refund_requests: amenity bookings too ----------
alter table public.refund_requests alter column term_id drop not null;
alter table public.refund_requests
  add column reservation_id bigint unique references public.reservations,
  add column kind text not null default 'membership' check (kind in ('membership', 'cancel', 'no_show')),
  add constraint refund_requests_target check ((kind = 'membership') = (term_id is not null) and (kind <> 'membership') = (reservation_id is not null));
-- the front desk reads the claims it decides
create policy refund_requests_desk_claims on public.refund_requests for select to authenticated
  using (public.is_active_user() and kind = 'no_show' and public.has_permission('payments:verify'));

-- ---------- the hold follows its payment ----------
create function public.settle_amenity_hold() returns trigger
language plpgsql security definer set search_path='' as $$
declare v public.reservations;
begin
  if new.status = old.status then return new; end if;
  select * into v from public.reservations where user_amenity_id = new.user_amenity_id and status = 'awaiting_payment' for update;
  if not found then return new; end if;
  if new.status = 'active' then
    if (select expires_at from public.payments where payment_id = v.payment_id) <= now() then perform public.fail('payment_expired', 'P0002'); end if;
    update public.reservations set status = 'confirmed' where reservation_id = v.reservation_id;
    if new.is_session then new.status := 'expired'; end if;   -- the session is this booking
    perform public.notify(v.member_id, 'amenity', 'Booking confirmed',
      to_char(v.starts_at at time zone 'Asia/Manila', 'Dy DD Mon, HH12:MI AM'), jsonb_build_object('reservation_id', v.reservation_id));
  elsif new.status = 'cancelled' then
    update public.reservations set status = 'cancelled', cancelled_at = now(), cancel_reason = 'Not paid' where reservation_id = v.reservation_id;
  end if;
  return new;
end $$;
revoke all on function public.settle_amenity_hold() from public, anon, authenticated;
create trigger user_amenities_settle_hold before update of status on public.user_amenities
  for each row execute function public.settle_amenity_hold();

-- Same as 0008, plus unpaid cash holds (pay at the desk) run out too.
create or replace function public.expire_payments() returns int
language plpgsql security definer set search_path='' as $$
declare v_pay record; v_count int := 0;
begin
  for v_pay in
    select p.payment_id, p.member_id, p.payment_code from public.payments p
    where p.status = 'pending' and p.expires_at <= now()
      and (p.method = 'qr' or exists (select 1 from public.reservations r where r.payment_id = p.payment_id))
    for update
  loop
    update public.payments set status = 'expired' where payment_id = v_pay.payment_id;
    perform public.cancel_payment_target(v_pay.payment_id);
    perform public.notify(v_pay.member_id, 'payment', 'Payment expired', v_pay.payment_code, jsonb_build_object('payment_id', v_pay.payment_id));
    v_count := v_count + 1;
  end loop;
  return v_count;
end $$;

-- ---------- F14: who runs which amenity ----------
create table public.coach_amenities (
  coach_id uuid not null references public.coaches (user_id),
  amenity_id bigint not null references public.amenities (amenity_id),
  primary key (amenity_id, coach_id)
);
alter table public.coach_amenities enable row level security;
revoke all on public.coach_amenities from public, anon, authenticated;
grant select on public.coach_amenities to authenticated;
create policy coach_amenities_read on public.coach_amenities for select to authenticated using (public.is_active_user());
comment on table public.coach_amenities is 'Coaches who run an amenity; written only by set_runs_amenity (0052)';

create function public.set_runs_amenity(p_amenity bigint, p_on boolean) returns void
language plpgsql security definer set search_path='' as $$
begin
  perform public.require_active_user();
  if not public.has_role('coach') then perform public.fail('forbidden', '42501'); end if;
  if p_on is null or not exists (select 1 from public.amenities where amenity_id = p_amenity and is_active) then perform public.fail('not_found', '22023'); end if;
  if p_on then
    insert into public.coach_amenities (coach_id, amenity_id) values (auth.uid(), p_amenity) on conflict do nothing;
  else
    delete from public.coach_amenities where coach_id = auth.uid() and amenity_id = p_amenity;
  end if;
  perform public.audit('amenity.runs', 'amenities', p_amenity::text, jsonb_build_object('on', p_on));
end $$;

create function public.coach_in_gym(p_amenity bigint) returns boolean
language sql stable security definer set search_path='' as $$
  select exists(select 1 from public.coach_visits v join public.profiles p on p.id = v.coach_id
    where v.checked_out_at is null and p.is_active
      and (not exists (select 1 from public.coach_amenities ca where ca.amenity_id = p_amenity)
           or exists (select 1 from public.coach_amenities ca where ca.amenity_id = p_amenity and ca.coach_id = v.coach_id)))
$$;
revoke all on function public.coach_in_gym(bigint) from public, anon, authenticated;

create or replace function public.guard_amenity_use() returns trigger
language plpgsql security definer set search_path='' as $$
begin
  if not public.coach_in_gym((select amenity_id from public.user_amenities where user_amenity_id = new.user_amenity_id)) then
    perform public.fail('no_coach_present', 'P0002');
  end if;
  return new;
end $$;

-- Same as 0043, with the presence rule per amenity (F14).
create or replace function public.book_slots(p_slots jsonb) returns setof public.reservations
language plpgsql security definer set search_path='' as $$
declare
  v_uid uuid := auth.uid(); v_open time; v_close time; v_day date; v_today date := (now() at time zone 'Asia/Manila')::date;
  b record; v_am public.amenities; v_res public.reservations; v_con text; v_pass public.user_amenities;
begin
  perform public.require_active_user();
  if not public.is_active_member(v_uid) then perform public.fail('no_active_membership', '22023'); end if;
  if p_slots is null or jsonb_typeof(p_slots) <> 'array' or jsonb_array_length(p_slots) not between 1 and 48
     or exists (select 1 from jsonb_array_elements(p_slots) e where e->>'amenity' is null or e->>'start' is null) then
    perform public.fail('bad_input', '22023');
  end if;
  if exists (select 1 from jsonb_array_elements(p_slots) e where extract(epoch from (e->>'start')::timestamptz)::bigint % 1800 <> 0) then
    perform public.fail('off_grid', '22023');
  end if;
  select opens_at, closes_at into v_open, v_close from public.gym_settings where id = 1;

  for b in
    with s as (select distinct (e->>'amenity')::bigint a, (e->>'start')::timestamptz t from jsonb_array_elements(p_slots) e),
    g as (select a, t, t - (row_number() over (partition by a order by t)) * interval '30 minutes' grp from s)
    select a, min(t) s, max(t) + interval '30 minutes' e, count(*) n from g group by a, grp order by min(t), a
  loop
    if b.n > 4 then perform public.fail('too_long', '22023'); end if;
    if b.s <= now() then perform public.fail('past_slot', '22023'); end if;
    v_day := (b.s at time zone 'Asia/Manila')::date;
    if v_day > v_today + 14 then perform public.fail('too_far_ahead', '22023'); end if;
    if b.s < (v_day + v_open) at time zone 'Asia/Manila' or b.e > (v_day + v_close) at time zone 'Asia/Manila' then
      perform public.fail('outside_gym_hours', '22023');
    end if;
    select * into v_am from public.amenities where amenity_id = b.a and is_active;
    if not found then perform public.fail('not_found', '22023'); end if;
    if v_am.status = 'unavailable' and (v_am.back_at is null or v_am.back_at > b.s) then perform public.fail('amenity_unavailable', 'P0002'); end if;
    if b.s <= now() + interval '30 minutes' and not public.coach_in_gym(b.a) then perform public.fail('no_coach_present', 'P0002'); end if;
    select * into v_pass from public.user_amenities ua where ua.member_id = v_uid and ua.amenity_id = b.a and ua.status = 'active'
      and (ua.expires_at is null or ua.expires_at >= b.e) order by ua.is_session, ua.user_amenity_id limit 1 for update;
    if not found then perform public.fail('no_pass', '22023'); end if;
    if exists (select 1 from public.training_sessions t where t.member_id = v_uid and t.status = 'scheduled'
               and tstzrange(t.scheduled_start, t.scheduled_end) && tstzrange(b.s, b.e)) then
      perform public.fail('member_busy', 'P0002');
    end if;
    begin
      insert into public.reservations (member_id, amenity_id, starts_at, ends_at, created_by, user_amenity_id)
      values (v_uid, b.a, b.s, b.e, v_uid, case when v_pass.is_session then v_pass.user_amenity_id end) returning * into v_res;
    exception when exclusion_violation then
      get stacked diagnostics v_con = constraint_name;
      perform public.reservation_clash(v_con);
    end;
    if v_pass.is_session then update public.user_amenities set status = 'expired' where user_amenity_id = v_pass.user_amenity_id; end if;
    perform public.audit('reservation.book', 'reservations', v_res.reservation_id::text,
      jsonb_build_object('amenity', b.a, 'starts_at', b.s, 'ends_at', b.e, 'session', v_pass.is_session));
    return next v_res;
  end loop;
end $$;

-- ---------- F12: book, then pay ----------
create function public.book_amenity(p_amenity bigint, p_start timestamptz, p_minutes integer, p_method public.payment_method default 'qr')
returns public.reservations
language plpgsql security definer set search_path='' as $$
declare
  v_uid uuid := auth.uid(); v_end timestamptz; v_day date; v_open time; v_close time; v_hours int;
  v_am public.amenities; v_ua bigint; v_pay public.payments; v_res public.reservations; v_con text;
begin
  perform public.require_active_user();
  if not public.is_active_member(v_uid) then perform public.fail('no_active_membership', '22023'); end if;
  if p_amenity is null or p_start is null or p_minutes is null or p_minutes not in (30, 60, 90, 120) or p_method is null or p_method not in ('qr', 'cash') then
    perform public.fail('bad_input', '22023');
  end if;
  perform public.expire_payments();   -- holds that ran out since the job last ran free their slots first
  v_end := p_start + make_interval(mins => p_minutes);

  -- a pass or an unused session still books straight away (book_slots checks everything)
  if exists (select 1 from public.user_amenities ua where ua.member_id = v_uid and ua.amenity_id = p_amenity and ua.status = 'active'
             and (ua.expires_at is null or ua.expires_at >= v_end)) then
    select * into v_res from public.book_slots((select jsonb_agg(jsonb_build_object('amenity', p_amenity, 'start', t))
      from generate_series(p_start, v_end - interval '30 minutes', interval '30 minutes') t));
    return v_res;
  end if;

  if extract(epoch from p_start)::bigint % 1800 <> 0 then perform public.fail('off_grid', '22023'); end if;
  if p_start <= now() then perform public.fail('past_slot', '22023'); end if;
  if p_start - interval '1 hour' <= now() then perform public.fail('too_soon', '22023'); end if;
  v_day := (p_start at time zone 'Asia/Manila')::date;
  if v_day > (now() at time zone 'Asia/Manila')::date + 14 then perform public.fail('too_far_ahead', '22023'); end if;
  select opens_at, closes_at, payment_expiry_hours into v_open, v_close, v_hours from public.gym_settings where id = 1;
  if p_start < (v_day + v_open) at time zone 'Asia/Manila' or v_end > (v_day + v_close) at time zone 'Asia/Manila' then
    perform public.fail('outside_gym_hours', '22023');
  end if;
  select * into v_am from public.amenities where amenity_id = p_amenity and is_active;
  if not found then perform public.fail('not_found', '22023'); end if;
  if v_am.status = 'unavailable' and (v_am.back_at is null or v_am.back_at > p_start) then perform public.fail('amenity_unavailable', 'P0002'); end if;
  if exists (select 1 from public.training_sessions t where t.member_id = v_uid and t.status = 'scheduled'
             and tstzrange(t.scheduled_start, t.scheduled_end) && tstzrange(p_start, v_end)) then
    perform public.fail('member_busy', 'P0002');
  end if;

  insert into public.user_amenities (member_id, amenity_id) values (v_uid, p_amenity) returning user_amenity_id into v_ua;
  insert into public.payments (payment_code, kind, method, amount, member_id, user_amenity_id, expires_at)
  values (public.generate_payment_code(), 'amenity', p_method, v_am.price, v_uid, v_ua,
          least(now() + make_interval(hours => v_hours), p_start - interval '1 hour'))
  returning * into v_pay;
  begin
    insert into public.reservations (member_id, amenity_id, starts_at, ends_at, created_by, user_amenity_id, payment_id, status)
    values (v_uid, p_amenity, p_start, v_end, v_uid, v_ua, v_pay.payment_id, 'awaiting_payment') returning * into v_res;
  exception when exclusion_violation then
    get stacked diagnostics v_con = constraint_name;
    perform public.reservation_clash(v_con);
  end;
  perform public.audit('reservation.hold', 'reservations', v_res.reservation_id::text,
    jsonb_build_object('amenity', p_amenity, 'starts_at', p_start, 'ends_at', v_end, 'payment_id', v_pay.payment_id, 'method', p_method));
  return v_res;
end $$;

-- ---------- F13: cancel and refund ----------
-- Same as 0043, plus: an unpaid hold is withdrawn with its payment; a paid booking (book_amenity) is refunded instead of giving a
-- session back, and the member can cancel it only 1 hour or more before the start (staff and the coach on it can cancel any time).
create or replace function public.cancel_reservation(p_id bigint, p_reason text) returns void
language plpgsql security definer set search_path='' as $$
declare v_uid uuid := auth.uid(); v public.reservations; v_pay public.payments; v_req bigint;
begin
  perform public.require_active_user();
  select * into v from public.reservations where reservation_id = p_id for update;
  if not found or (v.member_id <> v_uid and v.coach_id is distinct from v_uid and not public.is_staff()) then
    perform public.fail('not_found', '22023');
  end if;
  if v.member_id <> v_uid and length(btrim(coalesce(p_reason, ''))) not between 1 and 500 then perform public.fail('bad_reason', '22023'); end if;
  if v.status not in ('confirmed', 'awaiting_payment') then perform public.fail('already_processed', 'P0002'); end if;
  if v.starts_at <= now() then perform public.fail('already_started', 'P0002'); end if;
  if v.payment_id is not null then select * into v_pay from public.payments where payment_id = v.payment_id for update; end if;
  if v.status = 'confirmed' and v_pay.status = 'verified' and v.member_id = v_uid and v.starts_at - interval '1 hour' < now() then
    perform public.fail('too_late_to_cancel', 'P0002');
  end if;

  update public.reservations set status = 'cancelled', cancelled_at = now(), cancelled_by = v_uid, cancel_reason = nullif(btrim(p_reason), '')
    where reservation_id = p_id;
  if v.status = 'awaiting_payment' then
    update public.payments set status = 'rejected', rejected_reason = 'Booking cancelled' where payment_id = v.payment_id and status = 'pending';
    perform public.cancel_payment_target(v.payment_id);
  elsif v_pay.status = 'verified' then
    insert into public.refund_requests (kind, reservation_id, payment_id, member_id, amount, reason, status, decided_by, decided_at, decision_reason)
    values ('cancel', p_id, v_pay.payment_id, v.member_id, v_pay.amount, coalesce(nullif(btrim(p_reason), ''), 'Booking cancelled'),
            'approved', v_uid, now(), 'Cancelled 1 hour or more before the start: full refund')
    returning refund_request_id into v_req;
    perform public.notify_staff('payment', 'Booking refund to repay', v_pay.payment_code, jsonb_build_object('refund_request_id', v_req));
    perform public.audit('refund.request', 'refund_requests', v_req::text, jsonb_build_object('amount', v_pay.amount, 'reservation_id', p_id, 'kind', 'cancel'));
  elsif v.user_amenity_id is not null then
    update public.user_amenities set status = 'active' where user_amenity_id = v.user_amenity_id;
  end if;
  if v.member_id <> v_uid then
    perform public.notify(v.member_id, 'amenity', 'Booking cancelled', btrim(p_reason), jsonb_build_object('reservation_id', p_id));
  end if;
  perform public.audit('reservation.cancel', 'reservations', p_id::text, jsonb_build_object('reason', nullif(btrim(p_reason), '')));
end $$;

create function public.claim_amenity_no_show(p_id bigint, p_reason text) returns public.refund_requests
language plpgsql security definer set search_path='' as $$
declare v public.reservations; v_pay public.payments; v_req public.refund_requests;
begin
  perform public.require_active_user();
  if length(btrim(coalesce(p_reason, ''))) not between 1 and 1000 then perform public.fail('bad_input', '22023'); end if;
  select * into v from public.reservations where reservation_id = p_id and member_id = auth.uid() for update;
  if not found then perform public.fail('not_found', '22023'); end if;
  if v.payment_id is not null then select * into v_pay from public.payments where payment_id = v.payment_id for update; end if;
  if v.status <> 'confirmed' or v_pay.status is distinct from 'verified' then perform public.fail('not_refundable', 'P0002'); end if;
  if now() < v.ends_at then perform public.fail('not_ended', 'P0002'); end if;
  if now() > v.ends_at + interval '24 hours' then perform public.fail('claim_window_closed', '22023'); end if;
  if exists (select 1 from public.refund_requests where reservation_id = p_id) then perform public.fail('already_requested', 'P0002'); end if;
  insert into public.refund_requests (kind, reservation_id, payment_id, member_id, amount, reason)
  values ('no_show', p_id, v_pay.payment_id, v.member_id, v_pay.amount, btrim(p_reason)) returning * into v_req;
  perform public.notify_staff('payment', 'Booking refund claim', v_pay.payment_code, jsonb_build_object('refund_request_id', v_req.refund_request_id));
  perform public.audit('refund.request', 'refund_requests', v_req.refund_request_id::text, jsonb_build_object('amount', v_pay.amount, 'reservation_id', p_id, 'kind', 'no_show'));
  return v_req;
end $$;

-- The front desk (payments:verify) checks check-ins and the amenity's status, then decides with a reason.
create function public.decide_amenity_refund(p_request_id bigint, p_approve boolean, p_reason text) returns void
language plpgsql security definer set search_path='' as $$
declare v public.refund_requests; v_pay public.payments;
begin
  perform public.require_permission('payments:verify');
  if p_approve is null or length(btrim(coalesce(p_reason, ''))) not between 1 and 1000 then perform public.fail('bad_input', '22023'); end if;
  select * into v from public.refund_requests where refund_request_id = p_request_id and kind = 'no_show';
  if not found then perform public.fail('not_found', '22023'); end if;
  if v.member_id = auth.uid() then perform public.fail('self_decision', '42501'); end if;
  select * into v_pay from public.payments where payment_id = v.payment_id for update;
  select * into v from public.refund_requests where refund_request_id = p_request_id for update;
  if v.status <> 'pending' then perform public.fail('already_processed', 'P0002'); end if;
  if v_pay.status <> 'verified' or v.amount <> v_pay.amount then perform public.fail('not_refundable', 'P0002'); end if;
  update public.refund_requests set status = case when p_approve then 'approved' else 'rejected' end,
    decided_by = auth.uid(), decided_at = now(), decision_reason = btrim(p_reason) where refund_request_id = p_request_id;
  perform public.notify(v.member_id, 'payment', case when p_approve then 'Refund approved' else 'Refund claim declined' end,
    btrim(p_reason), jsonb_build_object('refund_request_id', p_request_id));
  perform public.audit('refund.decide', 'refund_requests', p_request_id::text, jsonb_build_object('approved', p_approve, 'reason', btrim(p_reason)));
end $$;

-- Same as 0025, but only for membership requests: amenity claims are decided by decide_amenity_refund.
create or replace function public.decide_membership_refund(p_request_id bigint,p_approve boolean,p_reason text default null) returns void
language plpgsql security definer set search_path='' as $$
declare v_request public.refund_requests; v_pay public.payments;
begin
  perform public.require_permission('payments:void');
  if p_approve is null or length(btrim(coalesce(p_reason,''))) not between 1 and 1000 then perform public.fail('bad_input','22023'); end if;
  select * into v_request from public.refund_requests where refund_request_id=p_request_id and kind='membership';
  if not found then perform public.fail('not_found','22023'); end if;
  if v_request.member_id=auth.uid() then perform public.fail('self_decision','42501'); end if;
  select * into v_pay from public.payments where payment_id=v_request.payment_id for update;
  perform 1 from public.members where user_id=v_request.member_id for update;
  select * into v_request from public.refund_requests where refund_request_id=p_request_id for update;
  if v_request.status<>'pending' then perform public.fail('already_processed','P0002'); end if;
  if v_pay.status<>'verified' or v_request.amount<>v_pay.amount then perform public.fail('not_refundable','P0002'); end if;
  if p_approve then
    update public.membership_terms set cancelled_at=now(),cancelled_by=auth.uid(),cancellation_reason='Refund approved'
      where term_id=v_request.term_id and cancelled_at is null;
    if not found then perform public.fail('already_processed','P0002'); end if;
    perform public.project_membership_terms(v_request.member_id);
  end if;
  update public.refund_requests set status=case when p_approve then 'approved' else 'rejected' end,
    decided_by=auth.uid(),decided_at=now(),decision_reason=btrim(p_reason) where refund_request_id=p_request_id;
  perform public.notify(v_request.member_id,'payment',case when p_approve then 'Cancellation approved; refund awaiting repayment' else 'Cancellation request rejected' end,
    btrim(p_reason),jsonb_build_object('refund_request_id',p_request_id));
  perform public.audit('refund.decide','refund_requests',p_request_id::text,jsonb_build_object('approved',p_approve,'reason',btrim(p_reason)));
end $$;

-- ---------- the grid: holds are taken too ----------
create or replace function public.amenity_grid(p_day date) returns jsonb
language plpgsql stable security definer set search_path='' as $$
declare v_open time; v_close time; v_from timestamptz; v_to timestamptz;
begin
  perform public.require_active_user();
  if p_day is null then perform public.fail('bad_input', '22023'); end if;
  select opens_at, closes_at into v_open, v_close from public.gym_settings where id = 1;
  v_from := p_day::timestamp at time zone 'Asia/Manila'; v_to := (p_day + 1)::timestamp at time zone 'Asia/Manila';
  return jsonb_build_object(
    'now', now(),
    'opens', (p_day + v_open) at time zone 'Asia/Manila',
    'closes', (p_day + v_close) at time zone 'Asia/Manila',
    'amenities', (select coalesce(jsonb_agg(jsonb_build_object('id', a.amenity_id, 'name', a.name, 'status', a.status, 'back', a.back_at) order by a.name), '[]')
                  from public.amenities a where a.is_active),
    'blocks', (select coalesce(jsonb_agg(jsonb_build_object('amenity', r.amenity_id, 'starts', r.starts_at, 'ends', r.ends_at,
                  'cooldown', public.plus_cooldown(r.ends_at), 'mine', r.member_id = auth.uid(),
                  'id', case when r.member_id = auth.uid() then r.reservation_id end) order by r.starts_at), '[]')
               from public.reservations r
               where r.status in ('confirmed', 'awaiting_payment') and tstzrange(r.starts_at, public.plus_cooldown(r.ends_at)) && tstzrange(v_from, v_to)));
end $$;

revoke all on function public.book_amenity(bigint, timestamptz, integer, public.payment_method), public.claim_amenity_no_show(bigint, text),
  public.decide_amenity_refund(bigint, boolean, text), public.set_runs_amenity(bigint, boolean) from public, anon;
grant execute on function public.book_amenity(bigint, timestamptz, integer, public.payment_method), public.claim_amenity_no_show(bigint, text),
  public.decide_amenity_refund(bigint, boolean, text), public.set_runs_amenity(bigint, boolean) to authenticated;
