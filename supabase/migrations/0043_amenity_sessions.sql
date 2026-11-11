-- Item 7 and D21 (2026-11-05). Reverses D6 and D9 of the 2026-10-20 plan: an amenity is sold per session and used only while a coach is
-- in the gym. The owner has not set session prices, and today's prices are for 30-day passes, so no amenity is converted here: an amenity
-- with duration_days set keeps selling as a pass until an admin enters its session price, which clears duration_days. Passes already
-- sold stay valid until they expire.

-- ---------- D21: activated means the member has checked in during the term ----------
create or replace function public.request_membership_refund(p_term_id bigint,p_reason text,p_category text default null) returns public.refund_requests
language plpgsql security definer set search_path='' as $$
declare v_term public.membership_terms; v_pay public.payments; v_request public.refund_requests; v_activated boolean;
begin
  perform public.require_active_user();
  if length(btrim(coalesce(p_reason,''))) not between 1 and 1000 then perform public.fail('bad_input','22023'); end if;
  if p_category is not null and p_category not in ('service_not_provided','misled','contract_allows','illness_or_relocation','change_of_mind') then
    perform public.fail('bad_input','22023');
  end if;
  select * into v_term from public.membership_terms where term_id=p_term_id and member_id=auth.uid();
  if not found then perform public.fail('not_found','22023'); end if;
  select * into v_pay from public.payments where payment_id=v_term.payment_id for update;
  if not found or v_pay.status<>'verified' or v_pay.amount<=0 then perform public.fail('not_refundable','P0002'); end if;
  perform 1 from public.members where user_id=auth.uid() for update;
  select * into v_term from public.membership_terms where term_id=p_term_id for update;
  if v_term.cancelled_at is not null then perform public.fail('not_refundable','P0002'); end if;
  if exists(select 1 from public.refund_requests where term_id=p_term_id) then perform public.fail('already_requested','P0002'); end if;
  if now()>=v_term.ends_at then perform public.fail('refund_window_closed','22023'); end if;
  v_activated := exists(select 1 from public.gym_check_ins c where c.member_id=auth.uid()
    and c.checked_in_at>=v_term.starts_at and c.checked_in_at<v_term.ends_at);
  if v_activated and p_category is null then perform public.fail('refund_reason_required','22023'); end if;
  insert into public.refund_requests(term_id,payment_id,member_id,amount,reason,was_activated,review_reason)
    values(p_term_id,v_pay.payment_id,auth.uid(),v_pay.amount,btrim(p_reason),v_activated,case when v_activated then p_category end) returning * into v_request;
  perform public.audit('refund.request','refund_requests',v_request.refund_request_id::text,
    jsonb_build_object('amount',v_pay.amount,'term_id',p_term_id,'activated',v_activated,'review_reason',v_request.review_reason));
  return v_request;
end $$;

-- ---------- a coach must be in the gym at the time of use ----------
create function public.coach_in_gym() returns boolean
language sql stable security definer set search_path='' as $$
  select exists(select 1 from public.coach_visits v join public.profiles p on p.id=v.coach_id where v.checked_out_at is null and p.is_active)
$$;
revoke all on function public.coach_in_gym() from public, anon, authenticated;

create function public.guard_amenity_use() returns trigger
language plpgsql security definer set search_path='' as $$
begin
  if not public.coach_in_gym() then perform public.fail('no_coach_present','P0002'); end if;
  return new;
end $$;
revoke all on function public.guard_amenity_use() from public, anon, authenticated;
create trigger amenity_usages_coach_guard before insert on public.amenity_usages for each row execute function public.guard_amenity_use();

-- ---------- one purchase of a per-session amenity is one session ----------
alter table public.user_amenities add column is_session boolean not null default false;
alter table public.reservations add column user_amenity_id bigint references public.user_amenities (user_amenity_id);

-- a trigger, so online purchases and counter payments are covered by one rule
create function public.mark_amenity_session() returns trigger
language plpgsql security definer set search_path='' as $$
begin
  new.is_session := exists(select 1 from public.amenities a where a.amenity_id=new.amenity_id and a.duration_days is null);
  return new;
end $$;
revoke all on function public.mark_amenity_session() from public, anon, authenticated;
create trigger user_amenities_session before insert on public.user_amenities for each row execute function public.mark_amenity_session();

-- Same as 0029, plus: a booking that starts within the next slot needs a coach in the gym now (a later one can't be checked yet), and a
-- booking made with a session uses it up. A timed pass is used first, so a member never spends a session while a pass still covers them.
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
    if b.s <= now() + interval '30 minutes' and not public.coach_in_gym() then perform public.fail('no_coach_present', 'P0002'); end if;
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

-- Same as 0029, plus: cancelling before the start gives the session back.
create or replace function public.cancel_reservation(p_id bigint, p_reason text) returns void
language plpgsql security definer set search_path='' as $$
declare v_uid uuid := auth.uid(); v public.reservations;
begin
  perform public.require_active_user();
  select * into v from public.reservations where reservation_id = p_id for update;
  if not found or (v.member_id <> v_uid and v.coach_id is distinct from v_uid and not public.is_staff()) then
    perform public.fail('not_found', '22023');
  end if;
  if v.member_id <> v_uid and length(btrim(coalesce(p_reason, ''))) not between 1 and 500 then perform public.fail('bad_reason', '22023'); end if;
  if v.status <> 'confirmed' then perform public.fail('already_processed', 'P0002'); end if;
  if v.starts_at <= now() then perform public.fail('already_started', 'P0002'); end if;
  update public.reservations set status = 'cancelled', cancelled_at = now(), cancelled_by = v_uid, cancel_reason = nullif(btrim(p_reason), '')
    where reservation_id = p_id;
  if v.user_amenity_id is not null then update public.user_amenities set status = 'active' where user_amenity_id = v.user_amenity_id; end if;
  if v.member_id <> v_uid then
    perform public.notify(v.member_id, 'amenity', 'Booking cancelled', btrim(p_reason), jsonb_build_object('reservation_id', p_id));
  end if;
  perform public.audit('reservation.cancel', 'reservations', p_id::text, jsonb_build_object('reason', nullif(btrim(p_reason), '')));
end $$;
