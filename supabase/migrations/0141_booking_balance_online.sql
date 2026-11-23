-- 0141 (owner, 2026-11-23): with a 50% down payment the rest is paid at the front desk. Until now the desk could only take it in cash
-- (into the drawer) or by GCash with a reference typed in; the owner wants the online option too. The desk starts an online payment
-- for the balance (Xendit's checkout opens on the desk PC, as for a walk-in, 0092); when Xendit confirms it, the booking's balance is
-- paid exactly as record_booking_balance would have marked it (reservations.balance_payment_id).

alter table public.payments add column if not exists balance_reservation_id bigint references public.reservations(reservation_id);
comment on column public.payments.balance_reservation_id is 'An online payment for this booking''s balance, taken at the front desk (0141)';

-- such a payment targets the booking, not an amenity pass: confirming it must not turn the member's used session back on (0120 rule
-- otherwise unchanged)
alter table public.payments drop constraint payments_target_matches_kind;
alter table public.payments add constraint payments_target_matches_kind check (
  case kind::text
    when 'membership'  then user_membership_package_id is not null and renewal_id is null and user_amenity_id is null
    when 'renewal'     then renewal_id is not null and user_membership_package_id is null and user_amenity_id is null
    when 'amenity'     then (user_amenity_id is not null or member_id is null or balance_reservation_id is not null)
                            and user_membership_package_id is null and renewal_id is null
    when 'walk_in'     then num_nonnulls(user_membership_package_id, renewal_id, user_amenity_id) = 0
    when 'coach_addon' then num_nonnulls(user_membership_package_id, renewal_id, user_amenity_id) = 0
  end);

create or replace function public.start_booking_balance_online(p_reservation_id bigint) returns public.payments
language plpgsql security definer set search_path = '' as $$
declare v public.reservations; v_pay public.payments;
begin
  perform public.require_permission('payments:record');
  select * into v from public.reservations where reservation_id = p_reservation_id for update;
  if not found then perform public.fail('not_found', '22023'); end if;
  if v.status <> 'confirmed' then perform public.fail('not_confirmed', 'P0002'); end if;
  if v.balance_payment_id is not null or v.total_price - v.down_payment <= 0 then perform public.fail('already_processed', 'P0002'); end if;
  -- one waiting online payment per balance: open it again instead of starting a second one
  select * into v_pay from public.payments
    where balance_reservation_id = p_reservation_id and status = 'pending' and expires_at > now() order by payment_id desc limit 1;
  if found then return v_pay; end if;
  insert into public.payments (payment_code, kind, method, status, amount, member_id, walk_in_name, recorded_by, expires_at, balance_reservation_id)
  values (public.generate_payment_code(), 'amenity', 'qr', 'pending', v.total_price - v.down_payment, v.member_id, v.walk_in_name,
          auth.uid(), now() + interval '30 minutes', p_reservation_id)
  returning * into v_pay;
  perform public.audit('reservation.balance_online', 'reservations', p_reservation_id::text,
    jsonb_build_object('payment_id', v_pay.payment_id, 'amount', v_pay.amount));
  return v_pay;
end $$;
revoke all on function public.start_booking_balance_online(bigint) from public, anon;
grant execute on function public.start_booking_balance_online(bigint) to authenticated;

-- Xendit confirming it (confirm_online_payment sets status 'verified') pays the balance, as the cash button does
create or replace function public.settle_booking_balance_online() returns trigger
language plpgsql security definer set search_path = '' as $$
begin
  if new.status = 'verified' and old.status is distinct from 'verified' and new.balance_reservation_id is not null then
    update public.reservations set balance_payment_id = new.payment_id
      where reservation_id = new.balance_reservation_id and balance_payment_id is null;
  end if;
  return new;
end $$;
revoke all on function public.settle_booking_balance_online() from public, anon, authenticated;
drop trigger if exists booking_balance_online on public.payments;
create trigger booking_balance_online after update of status on public.payments
  for each row execute function public.settle_booking_balance_online();

-- the cash/GCash button refuses while an online payment for the same balance is still waiting, so it can't be paid twice
do $$
declare v_def text;
begin
  v_def := pg_get_functiondef('public.record_booking_balance(bigint, public.payment_method, text)'::regprocedure);
  v_def := replace(v_def,
    $q$  if v.balance_payment_id is not null or v.total_price - v.down_payment <= 0 or v.user_amenity_id is null then$q$,
    $q$  if exists (select 1 from public.payments where balance_reservation_id = p_reservation_id and status = 'pending' and expires_at > now()) then
    perform public.fail('balance_online_pending', 'P0002');   -- 0141
  end if;
  if v.balance_payment_id is not null or v.total_price - v.down_payment <= 0 or v.user_amenity_id is null then$q$);
  if v_def not like '%balance_online_pending%' then raise exception '0141: record_booking_balance did not change'; end if;
  execute v_def;
end $$;
revoke all on function public.record_booking_balance(bigint, public.payment_method, text) from public, anon;
grant execute on function public.record_booking_balance(bigint, public.payment_method, text) to authenticated;
