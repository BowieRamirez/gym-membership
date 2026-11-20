-- Owner fixes 2026-11-19 Q1 = (b), Q9 (docs/superpowers/plans/2026-11-19-owner-fixes.md): the Day pass is a membership package.
-- Members buy it online like any package; the desk sells the same package to a member account. Its term ends at midnight Manila on
-- the day it is paid, so is_active_member (amenity bookings, time requests) lets a Day pass holder in for that day only.
-- A walk-in with no account still gets a walk_in payment (0081), now at the Day pass package's price.

alter table public.membership_packages add column is_day_pass boolean not null default false;
comment on column public.membership_packages.is_day_pass is 'The Day pass: its term ends at midnight Manila on the day it is paid (0098)';
create unique index membership_packages_one_day_pass on public.membership_packages (is_day_pass) where is_day_pass and is_active and published_at is not null;

do $$
declare v_def text; r record;
begin
  for r in select * from (values
    -- a Day pass is only for someone with no live or queued term; every package path (member, renewal, desk) comes through here
    ('public.create_membership_extension(uuid,bigint,text)',
     $q$  select * into v_disc from public.apply_discount(p_member,p_discount_code,v_pkg.price);$q$,
     $q$  if v_pkg.is_day_pass and exists(select 1 from public.membership_terms where member_id=p_member and cancelled_at is null and ends_at>now()) then
    perform public.fail('already_availed','P0002');   -- 0098
  end if;
  select * into v_disc from public.apply_discount(p_member,p_discount_code,v_pkg.price);$q$),
    -- the Day pass term ends at the next midnight in Manila
    ('public.activate_payment_target(bigint)',
     $q$    v_end:=v_start + make_interval(days=>v_pay.membership_duration_days);$q$,
     $q$    v_end:=v_start + make_interval(days=>v_pay.membership_duration_days);
    if exists(select 1 from public.membership_packages where membership_package_id=v_pay.membership_package_snapshot_id and is_day_pass) then
      v_end:=(((v_start at time zone 'Asia/Manila')::date + 1)::timestamp at time zone 'Asia/Manila');   -- 0098
    end if;$q$),
    -- a booking starts inside a membership term (a Day pass books today only)
    ('public.book_amenity_for(uuid,boolean,bigint,timestamptz,integer,public.payment_method,uuid,text)',
     $q$  v_end := p_start + make_interval(mins => p_minutes);$q$,
     $q$  v_end := p_start + make_interval(mins => p_minutes);
  if not exists(select 1 from public.membership_terms where member_id = v_uid and cancelled_at is null and starts_at <= p_start and ends_at > p_start) then
    perform public.fail('outside_membership', '22023');   -- 0098
  end if;$q$),
    -- the walk-in Day pass costs the Day pass package's price (the old setting until one is on sale)
    ('public.record_counter_payment_before_terms(public.payment_kind,public.payment_method,uuid,bigint,text,text,text)',
     $q$select walk_in_price into v_price from public.gym_settings where id = 1;$q$,
     $q$select coalesce((select price from public.membership_packages where is_day_pass and is_active and published_at is not null), walk_in_price) into v_price from public.gym_settings where id = 1;$q$),
    ('public.desk_online_payment_body(public.payment_kind,public.payment_method,uuid,bigint,text,text,text)',
     $q$select walk_in_price into v_price from public.gym_settings where id = 1;$q$,
     $q$select coalesce((select price from public.membership_packages where is_day_pass and is_active and published_at is not null), walk_in_price) into v_price from public.gym_settings where id = 1;$q$)
  ) as t(fn, old_text, new_text) loop
    v_def := pg_get_functiondef(r.fn::regprocedure);
    if array_length(string_to_array(v_def, r.old_text), 1) <> 2 then raise exception '0098: % lacks "%"', r.fn, r.old_text; end if;
    execute replace(v_def, r.old_text, r.new_text);   -- create or replace keeps the owner and grants
  end loop;
end $$;

revoke all on function public.create_membership_extension(uuid, bigint, text), public.activate_payment_target(bigint),
  public.book_amenity_for(uuid, boolean, bigint, timestamptz, integer, public.payment_method, uuid, text),
  public.record_counter_payment_before_terms(public.payment_kind, public.payment_method, uuid, bigint, text, text, text),
  public.desk_online_payment_body(public.payment_kind, public.payment_method, uuid, bigint, text, text, text) from public, anon, authenticated;
