-- Phase 16 (E13, Q1): Coach access add-on for active membership terms (scaled by days left, including Day pass).

alter table public.membership_terms
  add column if not exists coach_access_at timestamptz,
  add column if not exists coach_access_payment_id bigint references public.payments(payment_id);

-- Read-only offer for the signed-in member's current active term.
create or replace function public.coach_access_offer()
returns table(
  offered boolean,
  term_id bigint,
  package_id bigint,
  package_name text,
  full_price numeric,
  price numeric,
  days_left int,
  duration_days int,
  ends_at timestamptz,
  is_day_pass boolean,
  reason text
)
language plpgsql security definer set search_path = '' as $$
declare
  v_uid       uuid := auth.uid();
  v_term      public.membership_terms;
  v_pkg       public.membership_packages;
  v_today     date;
  v_days_left int;
  v_price     numeric;
begin
  perform public.require_active_user();

  select * into v_term
  from public.membership_terms mt
  where mt.member_id = v_uid
    and mt.cancelled_at is null
    and mt.starts_at <= now()
    and mt.ends_at > now()
  order by mt.starts_at desc
  limit 1;

  if not found then
    return query select false, null::bigint, null::bigint, null::text, null::numeric, null::numeric,
      null::int, null::int, null::timestamptz, false, 'no_active_term'::text;
    return;
  end if;

  select * into v_pkg
  from public.membership_packages mp
  where mp.membership_package_id = v_term.membership_package_id;

  if v_pkg.includes_coach then
    return query select false, v_term.term_id, v_pkg.membership_package_id, v_term.package_name, null::numeric, null::numeric,
      null::int, v_pkg.duration_days, v_term.ends_at, v_pkg.is_day_pass, 'already_included'::text;
    return;
  end if;

  if v_pkg.coach_addon_price is null or v_pkg.coach_addon_price <= 0 then
    return query select false, v_term.term_id, v_pkg.membership_package_id, v_term.package_name, null::numeric, null::numeric,
      null::int, v_pkg.duration_days, v_term.ends_at, v_pkg.is_day_pass, 'not_offered'::text;
    return;
  end if;

  if v_term.coach_access_at is not null then
    return query select false, v_term.term_id, v_pkg.membership_package_id, v_term.package_name, v_pkg.coach_addon_price, null::numeric,
      null::int, v_pkg.duration_days, v_term.ends_at, v_pkg.is_day_pass, 'already_bought'::text;
    return;
  end if;

  if exists (
    select 1 from public.payments p
    where p.member_id = v_uid
      and p.kind::text = 'coach_addon'
      and p.status = 'pending'
      and p.expires_at > now()
  ) then
    return query select false, v_term.term_id, v_pkg.membership_package_id, v_term.package_name, v_pkg.coach_addon_price, null::numeric,
      null::int, v_pkg.duration_days, v_term.ends_at, v_pkg.is_day_pass, 'pending_payment'::text;
    return;
  end if;

  v_today := (now() at time zone 'Asia/Manila')::date;

  if v_pkg.is_day_pass then
    if not exists (
      select 1 from public.free_coaches(v_today) fc
      where coalesce(array_length(fc.free_hours, 1), 0) > 0
    ) then
      return query select false, v_term.term_id, v_pkg.membership_package_id, v_term.package_name, v_pkg.coach_addon_price, null::numeric,
        null::int, v_pkg.duration_days, v_term.ends_at, true, 'no_free_coaches'::text;
      return;
    end if;
    v_days_left := greatest(1, v_pkg.duration_days);
    v_price := ceil(v_pkg.coach_addon_price)::numeric(12,2);
  else
    v_days_left := least(v_pkg.duration_days, greatest(1, ((v_term.ends_at at time zone 'Asia/Manila')::date - v_today)));
    v_price := ceil(v_pkg.coach_addon_price * v_days_left::numeric / v_pkg.duration_days::numeric)::numeric(12,2);
  end if;

  return query select true, v_term.term_id, v_pkg.membership_package_id, v_term.package_name,
    v_pkg.coach_addon_price, v_price, v_days_left, v_pkg.duration_days, v_term.ends_at, v_pkg.is_day_pass, null::text;
end $$;

-- Purchase the coach access add-on for the member's current active term.
create or replace function public.buy_coach_access(p_code text default null)
returns public.payments
language plpgsql security definer set search_path = '' as $$
declare
  v_uid     uuid := auth.uid();
  v_offer   record;
  v_disc    record;
  v_expires timestamptz;
  v_free    boolean;
  v_pay     public.payments;
begin
  perform public.require_active_user();
  perform pg_catalog.pg_advisory_xact_lock(23010, pg_catalog.hashtext(v_uid::text));

  select * into v_offer from public.coach_access_offer();
  if not v_offer.offered then
    if v_offer.reason = 'pending_payment' then perform public.fail('pending_payment', 'P0002');
    elsif v_offer.reason = 'already_bought' then perform public.fail('already_bought', 'P0002');
    else perform public.fail('coach_not_offered', 'P0002');
    end if;
  end if;

  select * into v_disc from public.apply_discount(v_uid, p_code, v_offer.price);
  if v_disc.discount_id is not null and not exists (
    select 1 from public.discount_items i
    where i.discount_id = v_disc.discount_id and i.kind = 'coach'
  ) then
    perform public.fail('code_not_for_item', '22023');
  end if;

  v_expires := least(
    v_offer.ends_at,
    now() + make_interval(hours => (select payment_expiry_hours from public.gym_settings where id = 1))
  );
  v_free := (v_disc.amount = 0);

  insert into public.payments (
    payment_code, kind, method, status, amount, discount_id, discount_amount, member_id,
    expires_at, verified_by, verified_at, receipt_no,
    membership_package_snapshot_id, membership_package_name
  ) values (
    public.generate_payment_code(),
    'coach_addon',
    'qr',
    case when v_free then 'verified'::public.payment_status else 'pending'::public.payment_status end,
    v_disc.amount,
    v_disc.discount_id,
    v_disc.discount_amount,
    v_uid,
    v_expires,
    null,
    case when v_free then now() else null end,
    case when v_free then public.next_receipt_no() else null end,
    v_offer.package_id,
    v_offer.package_name
  ) returning * into v_pay;

  if v_free then
    perform public.activate_payment_target(v_pay.payment_id);
  end if;

  perform public.audit('coach_access.buy', 'payments', v_pay.payment_id::text,
    jsonb_build_object('term_id', v_offer.term_id, 'amount', v_pay.amount));
  return v_pay;
end $$;

-- Update check_discount_code so kind='coach' uses the member's scaled coach_access_offer price when offered.
do $$
declare v_def text;
begin
  v_def := pg_get_functiondef('public.check_discount_code(text,text,bigint)'::regprocedure);
  if position('select p.coach_addon_price into v_price from public.membership_packages p' in v_def) = 0 then
    raise exception '0129: check_discount_code lacks expected coach_addon_price lookup';
  end if;
  v_def := replace(
    v_def,
    $q$    select p.coach_addon_price into v_price from public.membership_packages p
      where p.membership_package_id = p_item and p.coach_addon_price is not null;$q$,
    $q$    select o.price into v_price from public.coach_access_offer() o
      where o.offered and o.package_id = p_item;
    if v_price is null then
      select p.coach_addon_price into v_price from public.membership_packages p
        where p.membership_package_id = p_item and p.coach_addon_price is not null;
    end if;$q$
  );
  execute v_def;
end $$;

-- Activate coach_addon payments on the member's current active term.
do $$
declare v_def text;
begin
  v_def := pg_get_functiondef('public.activate_payment_target(bigint)'::regprocedure);
  if position('elsif v_pay.kind::text = ''coach_addon'' then' in v_def) = 0 then
    raise exception '0129: activate_payment_target lacks expected coach_addon branch';
  end if;
  v_def := replace(
    v_def,
    $q$  elsif v_pay.kind::text = 'coach_addon' then
    update public.training_sessions set held_until = null where payment_id = p_payment_id and status = 'scheduled';$q$,
    $q$  elsif v_pay.kind::text = 'coach_addon' then
    update public.membership_terms
    set coach_access_at = now(),
        coach_access_payment_id = v_pay.payment_id
    where term_id = (
      select term_id from public.membership_terms
      where member_id = v_pay.member_id
        and cancelled_at is null
        and starts_at <= now()
        and ends_at > now()
      order by starts_at desc
      limit 1
    );
    update public.training_sessions set held_until = null where payment_id = p_payment_id and status = 'scheduled';$q$
  );
  execute v_def;
end $$;

-- Switch _book_coach_session_core from per-session paid add-on to term coach_access_at.
do $$
declare v_def text;
begin
  v_def := pg_get_functiondef('public._book_coach_session_core(uuid,uuid,timestamptz,int,text,text,boolean,uuid)'::regprocedure);
  if position('if not v_pkg.includes_coach and v_pkg.coach_addon_price is null then' in v_def) = 0 then
    raise exception '0129: _book_coach_session_core lacks expected coach_addon_price guard';
  end if;
  v_def := replace(
    v_def,
    'if not v_pkg.includes_coach and v_pkg.coach_addon_price is null then',
    'if not v_pkg.includes_coach and v_term.coach_access_at is null then'
  );
  v_def := replace(
    v_def,
    'if v_pkg.includes_coach then',
    'if v_pkg.includes_coach or v_term.coach_access_at is not null then'
  );
  execute v_def;
end $$;

revoke all on function public.coach_access_offer(), public.buy_coach_access(text),
  public.check_discount_code(text, text, bigint), public.activate_payment_target(bigint),
  public._book_coach_session_core(uuid, uuid, timestamptz, int, text, text, boolean, uuid) from public, anon, authenticated;
grant execute on function public.coach_access_offer(), public.buy_coach_access(text),
  public.check_discount_code(text, text, bigint) to authenticated;
