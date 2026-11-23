-- Phase 10 (E5, E6, E7, E12): Direct coach session booking, 15-min held add-on sessions, free_coaches, coach discount kind, and add-on refunds.

alter type public.payment_kind add value if not exists 'coach_addon';

alter table public.training_sessions
  add column if not exists payment_id bigint references public.payments(payment_id),
  add column if not exists held_until timestamptz,
  add column if not exists is_paid_addon boolean not null default false,
  add column if not exists cancelled_at timestamptz,
  add column if not exists cancelled_by uuid references auth.users(id),
  add column if not exists cancel_reason text;

alter table public.payments drop constraint payments_target_matches_kind;
alter table public.payments add constraint payments_target_matches_kind check (
  case kind::text
    when 'membership'  then user_membership_package_id is not null and renewal_id is null and user_amenity_id is null
    when 'renewal'     then renewal_id is not null and user_membership_package_id is null and user_amenity_id is null
    when 'amenity'     then (user_amenity_id is not null or member_id is null) and user_membership_package_id is null and renewal_id is null
    when 'walk_in'     then num_nonnulls(user_membership_package_id, renewal_id, user_amenity_id) = 0
    when 'coach_addon' then num_nonnulls(user_membership_package_id, renewal_id, user_amenity_id) = 0
  end);

alter table public.payments drop constraint payments_payer;
alter table public.payments add constraint payments_payer check (
  (kind::text in ('walk_in', 'amenity') and member_id is null and walk_in_name is not null)
  or (kind::text <> 'walk_in' and member_id is not null and walk_in_name is null));

-- Discount items: allow 'coach' kind
alter table public.discount_items drop constraint if exists discount_items_kind_check;
alter table public.discount_items add constraint discount_items_kind_check check (kind in ('package', 'amenity', 'coach'));

-- Refund requests: support coach_addon refunds
alter table public.refund_requests
  add column if not exists training_session_id bigint references public.training_sessions(training_session_id);
alter table public.refund_requests drop constraint if exists refund_requests_kind_check;
alter table public.refund_requests drop constraint if exists refund_requests_target;
alter table public.refund_requests
  add constraint refund_requests_kind_check check (kind in ('membership', 'cancel', 'no_show', 'online', 'day_pass', 'walk_in_booking', 'coach_addon')),
  add constraint refund_requests_target check (
    (kind = 'membership') = (term_id is not null)
    and (kind in ('cancel', 'no_show', 'walk_in_booking')) = (reservation_id is not null)
    and (kind = 'coach_addon') = (training_session_id is not null)
  );

-- Update guard_payment_discount_item to support coach_addon
create or replace function public.guard_payment_discount_item() returns trigger
language plpgsql security definer set search_path='' as $$
declare v_kind text; v_item bigint;
begin
  if new.discount_id is null or (tg_op = 'UPDATE' and new.discount_id is not distinct from old.discount_id) then return new; end if;
  if new.kind::text in ('membership', 'renewal') then
    v_kind := 'package'; v_item := new.membership_package_snapshot_id;
  elsif new.kind::text = 'coach_addon' then
    v_kind := 'coach'; v_item := 0;
  elsif new.user_amenity_id is not null then
    v_kind := 'amenity'; select ua.amenity_id into v_item from public.user_amenities ua where ua.user_amenity_id = new.user_amenity_id;
  end if;
  if v_kind is null or (v_kind <> 'coach' and v_item is null)
     or not exists (select 1 from public.discount_items i where i.discount_id = new.discount_id and i.kind = v_kind
                    and (i.item_id is null or (v_kind <> 'coach' and i.item_id = v_item))) then
    perform public.fail('code_not_for_item', '22023');
  end if;
  return new;
end $$;

-- Update create_discount to allow kind = 'coach'
create or replace function public.create_discount(p_code text, p_percent int, p_expires_at timestamptz, p_max_uses int, p_show boolean, p_items jsonb)
returns bigint
language plpgsql security definer set search_path='' as $$
declare v_code text := upper(btrim(coalesce(p_code, ''))); v_id bigint; v_item jsonb; v_kind text; v_ref bigint;
begin
  perform public.require_permission('discounts:manage');
  if v_code !~ '^[A-Z0-9][A-Z0-9_-]{2,29}$' or p_percent is null or p_percent not between 1 and 100
     or p_expires_at is null or p_expires_at <= now() then
    perform public.fail('bad_discount');
  end if;
  if p_max_uses is null or p_max_uses < 1 then perform public.fail('limit_required'); end if;
  if p_items is null or jsonb_typeof(p_items) <> 'array' or jsonb_array_length(p_items) = 0 then perform public.fail('no_items'); end if;
  if exists (select 1 from public.discounts d where upper(d.code) = v_code) then perform public.fail('code_taken', 'P0002'); end if;
  insert into public.discounts (code, percent, expires_at, max_uses, show_on_packages)
    values (v_code, p_percent, p_expires_at, p_max_uses, coalesce(p_show, true)) returning discount_id into v_id;
  for v_item in select * from jsonb_array_elements(p_items) loop
    v_kind := v_item ->> 'kind';
    if jsonb_typeof(v_item) <> 'object' or coalesce(v_kind, '') not in ('package', 'amenity', 'coach')
       or coalesce(jsonb_typeof(v_item -> 'id'), 'null') not in ('number', 'null') then
      perform public.fail('bad_input');
    end if;
    v_ref := case when v_kind = 'coach' then null else (v_item ->> 'id')::bigint end;
    if v_ref is not null and not (
         (v_kind = 'package' and exists (select 1 from public.membership_packages p where p.membership_package_id = v_ref and p.is_active and p.published_at is not null))
      or (v_kind = 'amenity' and exists (select 1 from public.amenities a where a.amenity_id = v_ref and a.is_active and a.published_at is not null))) then
      perform public.fail('item_not_found');
    end if;
    insert into public.discount_items (discount_id, kind, item_id) values (v_id, v_kind, v_ref) on conflict do nothing;
  end loop;
  perform public.audit('discount.create', 'discounts', v_id::text, jsonb_build_object('code', v_code, 'percent', p_percent, 'items', p_items));
  return v_id;
end $$;

-- Update check_discount_code and check_discount_code_for to support 'coach' (p_item = package_id for price lookup)
create or replace function public.check_discount_code(p_code text, p_kind text, p_item bigint)
returns table(percent int, amount_off numeric, you_pay numeric, reason text)
language plpgsql security definer set search_path = '' as $$
declare
  v_uid    uuid := auth.uid();
  v_price  numeric;
  v_disc   record;
  v_pct    int;
  v_reason text;
begin
  perform public.require_active_user();
  if p_code is null or btrim(p_code) = '' or char_length(btrim(p_code)) > 50
     or coalesce(p_kind, '') not in ('package', 'amenity', 'coach') or p_item is null or p_item < 1 then
    perform public.fail('bad_input', '22023');
  end if;

  if p_kind = 'package' then
    select p.price into v_price from public.membership_packages p
      where p.membership_package_id = p_item and p.is_active and p.published_at is not null;
  elsif p_kind = 'amenity' then
    select a.price into v_price from public.amenities a
      where a.amenity_id = p_item and a.is_active and a.published_at is not null;
  else
    select p.coach_addon_price into v_price from public.membership_packages p
      where p.membership_package_id = p_item and p.coach_addon_price is not null;
  end if;
  if not found or v_price is null then perform public.fail('not_found', '22023'); end if;

  perform pg_catalog.pg_advisory_xact_lock(23003, pg_catalog.hashtext(v_uid::text));
  delete from public.code_checks where checked_at < now() - interval '10 minutes';
  if (select count(*) from public.code_checks where member_id = v_uid) >= 20 then
    perform public.fail('too_many_checks', 'P0002');
  end if;
  insert into public.code_checks (member_id) values (v_uid);

  begin
    select * into v_disc from public.apply_discount(v_uid, p_code, v_price);
    if not exists (
      select 1 from public.discount_items i
      where i.discount_id = v_disc.discount_id and i.kind = p_kind
        and (i.item_id is null or (p_kind <> 'coach' and i.item_id = p_item))
    ) then
      perform public.fail('code_not_for_item', '22023');
    end if;
    select d.percent into v_pct from public.discounts d where d.discount_id = v_disc.discount_id;
    return query select v_pct, v_disc.discount_amount, v_disc.amount, null::text;
  exception when others then
    v_reason := sqlerrm;
    if v_reason = 'invalid_code' and exists (
      select 1 from public.discounts d
      where upper(d.code) = upper(btrim(p_code)) and d.is_active and not d.paused and d.expires_at <= now()
    ) then
      v_reason := 'code_expired';
    end if;
    return query select null::int, null::numeric, null::numeric, v_reason;
  end;
end $$;

create or replace function public.check_discount_code_for(p_member uuid, p_code text, p_kind text, p_item bigint, p_price numeric default null)
returns table(percent int, amount_off numeric, you_pay numeric, reason text)
language plpgsql security definer set search_path = '' as $$
declare
  v_uid    uuid := auth.uid();
  v_price  numeric;
  v_disc   record;
  v_pct    int;
  v_reason text;
begin
  perform public.require_permission('payments:record');
  if p_member is null or p_code is null or btrim(p_code) = '' or char_length(btrim(p_code)) > 50
     or coalesce(p_kind, '') not in ('package', 'amenity', 'renewal', 'coach') or p_item is null or p_item < 1 then
    perform public.fail('bad_input', '22023');
  end if;
  if not exists (select 1 from public.members m where m.user_id = p_member) then perform public.fail('not_found', '22023'); end if;

  if p_kind = 'package' then
    select p.price into v_price from public.membership_packages p
      where p.membership_package_id = p_item and p.is_active and p.published_at is not null;
  elsif p_kind = 'amenity' then
    select a.price into v_price from public.amenities a
      where a.amenity_id = p_item and a.is_active and a.published_at is not null;
  elsif p_kind = 'coach' then
    if p_price is not null and p_price > 0 then v_price := p_price;
    else select p.coach_addon_price into v_price from public.membership_packages p where p.membership_package_id = p_item;
    end if;
  else
    v_price := p_price;
  end if;
  if v_price is null or v_price <= 0 then perform public.fail('not_found', '22023'); end if;

  perform pg_catalog.pg_advisory_xact_lock(23003, pg_catalog.hashtext(v_uid::text));
  delete from public.code_checks where checked_at < now() - interval '10 minutes';
  if (select count(*) from public.code_checks where member_id = v_uid) >= 20 then
    perform public.fail('too_many_checks', 'P0002');
  end if;
  insert into public.code_checks (member_id) values (v_uid);

  begin
    select * into v_disc from public.apply_discount(p_member, p_code, v_price);
    if p_kind <> 'renewal' and not exists (
      select 1 from public.discount_items i
      where i.discount_id = v_disc.discount_id and i.kind = p_kind
        and (i.item_id is null or (p_kind <> 'coach' and i.item_id = p_item))
    ) then
      perform public.fail('code_not_for_item', '22023');
    end if;
    select d.percent into v_pct from public.discounts d where d.discount_id = v_disc.discount_id;
    return query select v_pct, v_disc.discount_amount, v_disc.amount, null::text;
  exception when others then
    v_reason := sqlerrm;
    if v_reason = 'invalid_code' and exists (
      select 1 from public.discounts d
      where upper(d.code) = upper(btrim(p_code)) and d.is_active and not d.paused and d.expires_at <= now()
    ) then
      v_reason := 'code_expired';
    end if;
    return query select null::int, null::numeric, null::numeric, v_reason;
  end;
end $$;

-- Update cancel_payment_target, activate_payment_target, expire_payments for held coach sessions
create or replace function public.cancel_payment_target(p_payment_id bigint) returns void
language plpgsql security definer set search_path = '' as $$
declare v_pay public.payments;
begin
  select * into v_pay from public.payments where payment_id = p_payment_id;
  update public.user_membership_packages set status = 'cancelled' where user_membership_package_id = v_pay.user_membership_package_id and status = 'pending';
  update public.membership_renewals set status = 'cancelled' where renewal_id = v_pay.renewal_id and status = 'pending';
  update public.user_amenities set status = 'cancelled' where user_amenity_id = v_pay.user_amenity_id and status = 'pending';
  update public.reservations set status = 'cancelled', cancelled_at = now(), cancel_reason = 'Not paid'
    where payment_id = p_payment_id and member_id is null and status = 'awaiting_payment';
  update public.training_sessions set status = 'cancelled', cancelled_at = now(), cancel_reason = 'Not paid'
    where payment_id = p_payment_id and status = 'scheduled' and held_until is not null;
end $$;

do $$
declare v_def text;
begin
  v_def := pg_get_functiondef('public.activate_payment_target(bigint)'::regprocedure);
  v_def := replace(v_def,
    $q$  elsif v_pay.user_amenity_id is not null then$q$,
    $q$  elsif v_pay.kind::text = 'coach_addon' then
    update public.training_sessions set held_until = null where payment_id = p_payment_id and status = 'scheduled';
  elsif v_pay.user_amenity_id is not null then$q$);
  execute v_def;
end $$;

create or replace function public.expire_payments() returns int
language plpgsql security definer set search_path='' as $$
declare v_pay record; v_count int := 0;
begin
  for v_pay in
    select p.payment_id, p.member_id, p.payment_code from public.payments p
    where p.status = 'pending' and p.expires_at <= now() and p.method in ('qr', 'cash')
    for update
  loop
    update public.payments set status = 'expired' where payment_id = v_pay.payment_id;
    perform public.cancel_payment_target(v_pay.payment_id);
    perform public.notify(v_pay.member_id, 'payment', 'Payment expired', v_pay.payment_code, jsonb_build_object('payment_id', v_pay.payment_id));
    v_count := v_count + 1;
  end loop;
  return v_count;
end $$;

-- free_coaches(p_day): coaches with a posted shift that day and their free whole hours (Manila 0..23)
create or replace function public.free_coaches(p_day date)
returns table(coach_id uuid, full_name text, specialty text, bio text, avatar_path text, free_hours int[])
language plpgsql stable security definer set search_path = '' as $$
declare
  v_open time; v_close time; v_max_h int;
  v_day_start timestamptz; v_day_end timestamptz;
begin
  perform public.require_active_user();
  if p_day is null then perform public.fail('bad_input', '22023'); end if;
  if exists (select 1 from public.gym_closures gc where gc.closed_on = p_day) then return; end if;

  select opens_at, closes_at, max_coach_hours_per_day into v_open, v_close, v_max_h from public.gym_settings where id = 1;
  v_day_start := (p_day + v_open) at time zone 'Asia/Manila';
  v_day_end   := (p_day + v_close) at time zone 'Asia/Manila';

  return query
  with coach_booked as (
    select ts.coach_id,
           coalesce(sum(extract(epoch from (ts.scheduled_end - ts.scheduled_start)) / 3600.0), 0) as hrs
    from public.training_sessions ts
    where ts.status in ('scheduled', 'completed')
      and (ts.held_until is null or ts.held_until > now())
      and (ts.scheduled_start at time zone 'Asia/Manila')::date = p_day
    group by ts.coach_id
  ),
  hours as (
    select h,
           ((p_day + make_interval(hours => h)) at time zone 'Asia/Manila') as slot_start,
           ((p_day + make_interval(hours => h + 1)) at time zone 'Asia/Manila') as slot_end
    from generate_series(0, 23) h
  ),
  free_slots as (
    select sp.coach_id, hrs.h
    from public.shift_posts sp
    join hours hrs on sp.starts_at <= hrs.slot_start and sp.ends_at >= hrs.slot_end
    where sp.status = 'scheduled'
      and hrs.slot_start > now()
      and hrs.slot_start >= v_day_start and hrs.slot_end <= v_day_end
      and not exists (
        select 1 from public.training_sessions ts
        where ts.coach_id = sp.coach_id
          and ts.status = 'scheduled'
          and (ts.held_until is null or ts.held_until > now())
          and tstzrange(ts.scheduled_start, ts.scheduled_end) && tstzrange(hrs.slot_start, hrs.slot_end)
      )
  )
  select c.user_id, p.full_name, c.specialty, c.bio, p.avatar_path,
         array_agg(distinct fs.h order by fs.h)
  from public.coaches c
  join public.profiles p on p.id = c.user_id and p.is_active
  join free_slots fs on fs.coach_id = c.user_id
  left join coach_booked cb on cb.coach_id = c.user_id
  where v_max_h is null or coalesce(cb.hrs, 0) < v_max_h
  group by c.user_id, p.full_name, c.specialty, c.bio, p.avatar_path
  order by p.full_name;
end $$;

-- Shared internal helper for booking a coach session (member self-service or desk)
create or replace function public._book_coach_session_core(
  p_member uuid,
  p_coach uuid,
  p_start timestamptz,
  p_hours int,
  p_method text,
  p_code text,
  p_skip_e6 boolean,
  p_recorded_by uuid
) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  v_end timestamptz;
  v_day date;
  v_open time; v_close time;
  v_max_day int; v_max_up int; v_max_h int;
  v_term public.membership_terms;
  v_pkg public.membership_packages;
  v_booked_hrs numeric;
  v_no_shows int; v_last_ns timestamptz; v_days_left int;
  v_cnt_day int; v_cnt_up int;
  v_base numeric; v_disc_id bigint; v_disc_amt numeric := 0; v_amt numeric := 0; v_disc record;
  v_pay_id bigint; v_pay_code text; v_held timestamptz; v_sid bigint;
  v_verified boolean := false;
  v_con text;
begin
  perform public.expire_payments();
  if p_coach is null or p_start is null or p_hours not in (1, 2) then
    perform public.fail('bad_input', '22023');
  end if;
  if extract(epoch from p_start)::bigint % 3600 <> 0 then
    perform public.fail('off_grid', '22023');
  end if;
  if p_start <= now() then perform public.fail('past_slot', '22023'); end if;
  v_end := p_start + make_interval(hours => p_hours);
  v_day := (p_start at time zone 'Asia/Manila')::date;

  if exists (select 1 from public.gym_closures where closed_on = v_day) then
    perform public.fail('gym_closed', 'P0002');
  end if;
  select opens_at, closes_at, max_coach_sessions_per_day, max_upcoming_coach_sessions, max_coach_hours_per_day
    into v_open, v_close, v_max_day, v_max_up, v_max_h from public.gym_settings where id = 1;
  if p_start < (v_day + v_open) at time zone 'Asia/Manila' or v_end > (v_day + v_close) at time zone 'Asia/Manila' then
    perform public.fail('outside_gym_hours', '22023');
  end if;

  if not exists (select 1 from public.coaches c join public.profiles pr on pr.id = c.user_id and pr.is_active where c.user_id = p_coach) then
    perform public.fail('not_found', '22023');
  end if;
  if not exists (select 1 from public.shift_posts sp where sp.coach_id = p_coach and sp.status = 'scheduled' and sp.starts_at <= p_start and sp.ends_at >= v_end) then
    perform public.fail('coach_not_free', 'P0002');
  end if;

  -- Active membership covering [p_start, v_end]
  select * into v_term from public.membership_terms mt
    where mt.member_id = p_member and mt.cancelled_at is null and mt.starts_at <= p_start and mt.ends_at >= v_end
    order by mt.ends_at desc limit 1;
  if not found then perform public.fail('outside_membership', 'P0002'); end if;

  select * into v_pkg from public.membership_packages mp where mp.membership_package_id = v_term.membership_package_id;
  if not v_pkg.includes_coach and v_pkg.coach_addon_price is null then
    perform public.fail('coach_not_offered', 'P0002');
  end if;

  -- Lock coach to serialize concurrent bookings and check E12 daily max
  perform 1 from public.coaches where user_id = p_coach for update;
  select coalesce(sum(extract(epoch from (ts.scheduled_end - ts.scheduled_start)) / 3600.0), 0) into v_booked_hrs
    from public.training_sessions ts
    where ts.coach_id = p_coach and ts.status in ('scheduled', 'completed')
      and (ts.scheduled_start at time zone 'Asia/Manila')::date = v_day;
  if v_max_h is not null and v_booked_hrs + p_hours > v_max_h then
    perform public.fail('coach_day_full', 'P0002');
  end if;

  if v_pkg.includes_coach then
    if not p_skip_e6 then
      select count(*), max(ts.scheduled_start) into v_no_shows, v_last_ns
        from public.training_sessions ts
        where ts.member_id = p_member and ts.status = 'no_show' and not ts.is_paid_addon
          and ts.scheduled_start >= now() - interval '30 days';
      if v_no_shows >= 2 and v_last_ns + interval '7 days' > now() then
        v_days_left := greatest(1, ceil(extract(epoch from (v_last_ns + interval '7 days' - now())) / 86400.0)::int);
        raise exception 'coach_booking_blocked' using errcode = 'P0002', detail = v_days_left::text;
      end if;

      select count(*) into v_cnt_day from public.training_sessions ts
        where ts.member_id = p_member and not ts.is_paid_addon and ts.status in ('scheduled', 'completed')
          and (ts.scheduled_start at time zone 'Asia/Manila')::date = v_day;
      if v_cnt_day >= v_max_day then perform public.fail('too_many_coach_sessions', 'P0002'); end if;

      select count(*) into v_cnt_up from public.training_sessions ts
        where ts.member_id = p_member and not ts.is_paid_addon and ts.status = 'scheduled' and ts.scheduled_start > now();
      if v_cnt_up >= v_max_up then perform public.fail('too_many_coach_sessions', 'P0002'); end if;
    end if;

    begin
      insert into public.training_sessions (coach_id, member_id, title, scheduled_start, scheduled_end, status, is_paid_addon)
      values (p_coach, p_member, 'Coach session', p_start, v_end, 'scheduled', false)
      returning training_session_id into v_sid;
    exception when exclusion_violation then
      get stacked diagnostics v_con = constraint_name;
      if v_con = 'training_sessions_no_member_overlap' then perform public.fail('member_busy', 'P0002');
      else perform public.fail('coach_taken', 'P0002');
      end if;
    end;

    perform public.audit('coach_session.book', 'training_sessions', v_sid::text, jsonb_build_object('coach_id', p_coach, 'start', p_start, 'hours', p_hours, 'free', true));
    return jsonb_build_object('training_session_id', v_sid, 'payment_id', null, 'payment_code', null, 'amount', 0, 'held_until', null, 'is_free', true);
  end if;

  -- Paid add-on path
  v_base := v_pkg.coach_addon_price * p_hours;
  if nullif(btrim(coalesce(p_code, '')), '') is not null then
    select * into v_disc from public.apply_discount(p_member, p_code, v_base);
    v_disc_id := v_disc.discount_id;
    v_disc_amt := v_disc.discount_amount;
    v_amt := v_disc.amount;
  else
    v_amt := v_base;
  end if;

  v_verified := (p_method = 'cash' or v_amt = 0);
  v_held := case when v_verified then null else now() + interval '15 minutes' end;

  -- Insert session first inside transaction so exclusion_violation aborts before creating payment
  begin
    insert into public.training_sessions (coach_id, member_id, title, scheduled_start, scheduled_end, status, held_until, is_paid_addon)
    values (p_coach, p_member, 'Coach session', p_start, v_end, 'scheduled', v_held, true)
    returning training_session_id into v_sid;
  exception when exclusion_violation then
    get stacked diagnostics v_con = constraint_name;
    if v_con = 'training_sessions_no_member_overlap' then perform public.fail('member_busy', 'P0002');
    else perform public.fail('coach_taken', 'P0002');
    end if;
  end;

  v_pay_code := public.generate_payment_code();
  insert into public.payments (
    member_id, kind, method, status, amount, discount_id, discount_amount, payment_code,
    expires_at, recorded_by, verified_by, verified_at, receipt_no, membership_package_snapshot_id, membership_package_name
  ) values (
    p_member, 'coach_addon', p_method::public.payment_method,
    case when v_verified then 'verified'::public.payment_status else 'pending'::public.payment_status end,
    v_amt, v_disc_id, v_disc_amt, v_pay_code,
    v_held,
    p_recorded_by,
    case when v_verified then coalesce(p_recorded_by, p_member) else null end,
    case when v_verified then now() else null end,
    case when v_verified then public.next_receipt_no() else null end,
    v_pkg.membership_package_id, v_pkg.name
  ) returning payment_id into v_pay_id;

  update public.training_sessions set payment_id = v_pay_id where training_session_id = v_sid;
  perform public.audit('coach_session.book', 'training_sessions', v_sid::text, jsonb_build_object('coach_id', p_coach, 'start', p_start, 'hours', p_hours, 'payment_id', v_pay_id, 'amount', v_amt));
  return jsonb_build_object('training_session_id', v_sid, 'payment_id', v_pay_id, 'payment_code', v_pay_code, 'amount', v_amt, 'held_until', v_held, 'is_free', false);
end $$;

create or replace function public.book_coach_session(p_coach uuid, p_start timestamptz, p_hours int, p_code text default null)
returns jsonb
language plpgsql security definer set search_path = '' as $$
begin
  perform public.require_active_user();
  return public._book_coach_session_core(auth.uid(), p_coach, p_start, p_hours, 'qr', p_code, false, null);
end $$;

create or replace function public.desk_book_coach_session(p_member uuid, p_coach uuid, p_start timestamptz, p_hours int, p_method text, p_code text default null)
returns jsonb
language plpgsql security definer set search_path = '' as $$
begin
  perform public.require_permission('payments:record');
  if p_member is null or not exists (select 1 from public.members where user_id = p_member) then
    perform public.fail('not_found', '22023');
  end if;
  if coalesce(p_method, '') not in ('cash', 'qr', 'online') then perform public.fail('bad_input', '22023'); end if;
  return public._book_coach_session_core(p_member, p_coach, p_start, p_hours, case when p_method = 'online' then 'qr' else p_method end, p_code, true, auth.uid());
end $$;

-- Update cancel_session: refund paid add-on if >=24h ahead or coach/staff cancels; reject pending payment if held
create or replace function public.cancel_session(p_session_id bigint, p_reason text default null) returns void
language plpgsql security definer set search_path = '' as $$
declare
  v_uid uuid := auth.uid();
  v_s public.training_sessions;
  v_pay public.payments;
  v_mine boolean;
  v_refund boolean := false;
  v_req bigint;
begin
  perform public.require_active_user();
  select * into v_s from public.training_sessions where training_session_id = p_session_id for update;
  if not found or (v_s.coach_id <> v_uid and v_s.member_id <> v_uid and not public.is_staff()) then
    perform public.fail('not_found', '22023');
  end if;
  if v_s.status <> 'scheduled' then perform public.fail('already_processed', 'P0002'); end if;
  if v_s.scheduled_start <= now() then perform public.fail('already_started', 'P0002'); end if;

  v_mine := (v_s.member_id = v_uid);
  update public.training_sessions
    set status = 'cancelled', cancelled_at = now(), cancelled_by = v_uid,
        cancel_reason = nullif(btrim(p_reason), ''),
        notes = coalesce(nullif(btrim(p_reason), ''), notes)
    where training_session_id = p_session_id;

  update public.time_requests set status = 'cancelled' where training_session_id = p_session_id and status = 'pending';
  update public.reservations set status = 'cancelled', cancelled_at = now(), cancelled_by = v_uid,
    cancel_reason = coalesce(nullif(btrim(p_reason), ''), 'Session cancelled')
    where training_session_id = p_session_id and status = 'confirmed';

  if v_s.payment_id is not null then
    select * into v_pay from public.payments where payment_id = v_s.payment_id for update;
    if v_pay.status = 'pending' then
      update public.payments set status = 'rejected', rejected_reason = 'Session cancelled' where payment_id = v_pay.payment_id;
    elsif v_pay.status = 'verified' and coalesce(v_pay.amount, 0) > 0 then
      v_refund := (not v_mine) or (v_s.scheduled_start - interval '24 hours' >= now());
      if v_refund then
        insert into public.refund_requests (kind, training_session_id, payment_id, member_id, amount, reason, status, decided_by, decided_at, decision_reason)
        values ('coach_addon', p_session_id, v_pay.payment_id, v_s.member_id, v_pay.amount,
                coalesce(nullif(btrim(p_reason), ''), 'Coach session cancelled'),
                'approved', v_uid, now(),
                case when v_mine then 'Cancelled 24 hours or more before the start: coach add-on refunded'
                     else 'Cancelled by the coach or gym: coach add-on refunded' end)
        returning refund_request_id into v_req;
        perform public.notify_staff('payment', 'Coach add-on refund to repay', v_pay.payment_code, jsonb_build_object('refund_request_id', v_req));
      end if;
    end if;
  end if;

  if v_uid <> v_s.member_id then
    perform public.notify(v_s.member_id, 'system', 'Your coach session was cancelled', nullif(btrim(p_reason), ''), jsonb_build_object('training_session_id', p_session_id));
  end if;
  if v_uid <> v_s.coach_id then
    perform public.notify(v_s.coach_id, 'system', 'A training session was cancelled', nullif(btrim(p_reason), ''), jsonb_build_object('training_session_id', p_session_id));
  end if;
  perform public.audit('coach_session.cancel', 'training_sessions', p_session_id::text, jsonb_build_object('reason', nullif(btrim(p_reason), ''), 'refund', v_refund));
end $$;

revoke all on function public.guard_payment_discount_item(),
  public.create_discount(text, int, timestamptz, int, boolean, jsonb),
  public.check_discount_code(text, text, bigint),
  public.check_discount_code_for(uuid, text, text, bigint, numeric),
  public.cancel_payment_target(bigint),
  public.expire_payments(),
  public.free_coaches(date),
  public._book_coach_session_core(uuid, uuid, timestamptz, int, text, text, boolean, uuid),
  public.book_coach_session(uuid, timestamptz, int, text),
  public.desk_book_coach_session(uuid, uuid, timestamptz, int, text, text),
  public.cancel_session(bigint, text)
  from public, anon, authenticated;

grant execute on function
  public.create_discount(text, int, timestamptz, int, boolean, jsonb),
  public.check_discount_code(text, text, bigint),
  public.check_discount_code_for(uuid, text, text, bigint, numeric),
  public.free_coaches(date),
  public.book_coach_session(uuid, timestamptz, int, text),
  public.desk_book_coach_session(uuid, uuid, timestamptz, int, text, text),
  public.cancel_session(bigint, text)
  to authenticated;
