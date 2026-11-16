-- Cashier plan Y6 (docs/superpowers/plans/2026-11-16-cashier-walkin-logs.md): an e-wallet payment at the desk goes through Xendit.
-- The desk records a PENDING online (qr) payment for 30 minutes; Xendit's checkout opens on the desk PC, the customer scans its QR Ph
-- (GCash, Maya or any bank app), and confirm_online_payment activates it like any online payment. The cash drawer is never involved.
--
-- start_desk_online_payment is record_counter_payment (0005, with 0018's guards, 0055 and 0061's changes) with a pending payment and
-- no activation, made from the live definition the way 0061 changes long bodies, so the prices, discounts and checks stay one code.

-- a day pass may now be an online payment, as long as the desk recorded it
alter table public.payments drop constraint payments_walk_in_at_counter;
alter table public.payments add constraint payments_walk_in_at_counter check (kind <> 'walk_in' or method <> 'qr' or recorded_by is not null);

do $$
declare v_def text; r record;
begin
  v_def := pg_get_functiondef('public.record_counter_payment_before_terms(public.payment_kind,public.payment_method,uuid,bigint,text,text,text)'::regprocedure);
  for r in select * from (values
    ('public.record_counter_payment_before_terms(', 'public.desk_online_payment_body('),
    ($q$if p_method = 'qr' then perform public.fail('bad_input', '22023'); end if;$q$, $q$if p_method <> 'qr' then perform public.fail('bad_input', '22023'); end if;$q$),
    ($q$'verified', v_disc.amount$q$, $q$'pending', v_disc.amount$q$),
    ('recorded_by, verified_by, verified_at, receipt_no', 'recorded_by, verified_by, verified_at, receipt_no, expires_at'),
    ('v_uid, v_uid, now(), public.next_receipt_no()', $q$v_uid, null, null, null, now() + interval '30 minutes'$q$),
    ('insert into public.gym_check_ins (payment_id, recorded_by) values (v_pay.payment_id, v_uid);', 'null;   -- checked in when Xendit confirms'),
    ('perform public.activate_payment_target(v_pay.payment_id);', 'null;   -- activated when Xendit confirms'),
    ($q$perform public.notify(p_member_id, 'payment', 'Payment received', v_pay.payment_code, jsonb_build_object('payment_id', v_pay.payment_id));$q$, 'null;'),
    ($q$perform public.audit('payment.record',$q$, $q$perform public.audit('payment.desk_online',$q$)
  ) as t(old_text, new_text) loop
    if array_length(string_to_array(v_def, r.old_text), 1) <> 2 then
      raise exception '0080: record_counter_payment_before_terms does not contain "%" exactly once', r.old_text;
    end if;
    v_def := replace(v_def, r.old_text, r.new_text);
  end loop;
  execute v_def;
end $$;

-- the desk's entry point: 0018's guards for a member, then the body above with method qr
create function public.start_desk_online_payment(p_kind public.payment_kind, p_member_id uuid default null, p_item_id bigint default null,
  p_walk_in_name text default null, p_discount_code text default null) returns public.payments
language plpgsql security definer set search_path='' as $$
begin
  perform public.require_permission('payments:record');
  if p_member_id is not null then
    perform 1 from public.members where user_id = p_member_id for update;
    if p_kind in ('membership','renewal') and exists(select 1 from public.payments
      where member_id = p_member_id and status = 'pending' and kind in ('membership','renewal')) then
      perform public.fail('renewal_pending', 'P0002');
    end if;
    if p_kind = 'membership' and exists(select 1 from public.membership_terms
      where member_id = p_member_id and cancelled_at is null and ends_at > now()) then
      perform public.fail('already_availed', 'P0002');
    end if;
  end if;
  return public.desk_online_payment_body(p_kind, 'qr', p_member_id, p_item_id, p_walk_in_name, null, p_discount_code);
end $$;

-- 0078's confirm_online_payment, plus: a day pass paid online at the desk checks the person in, as a cash one does; it has no member,
-- so nobody is notified and the log names the desk member who took it
do $$
declare v_def text; r record;
begin
  v_def := pg_get_functiondef('public.confirm_online_payment(text,numeric,text)'::regprocedure);
  for r in select * from (values
    ($q$  perform public.activate_payment_target(v.payment_id);$q$,
     $q$  perform public.activate_payment_target(v.payment_id);
  if v.kind = 'walk_in' then insert into public.gym_check_ins (payment_id, recorded_by) values (v.payment_id, v.recorded_by); end if;$q$),
    ($q$perform public.notify(v.member_id, 'payment', 'Payment received'$q$,
     $q$if v.member_id is not null then perform public.notify(v.member_id, 'payment', 'Payment received'$q$),
    ($q$jsonb_build_object('payment_id', v.payment_id));
  perform public.audit_as(v.member_id, 'payment.online',$q$,
     $q$jsonb_build_object('payment_id', v.payment_id)); end if;
  perform public.audit_as(coalesce(v.member_id, v.recorded_by), 'payment.online',$q$),
    ($q$perform public.audit_as(v.member_id, 'payment.online_refund',$q$, $q$perform public.audit_as(coalesce(v.member_id, v.recorded_by), 'payment.online_refund',$q$)
  ) as t(old_text, new_text) loop
    if array_length(string_to_array(v_def, r.old_text), 1) <> 2 then raise exception '0080: confirm_online_payment lacks "%"', r.old_text; end if;
    v_def := replace(v_def, r.old_text, r.new_text);
  end loop;
  execute v_def;
end $$;

revoke all on function public.desk_online_payment_body(public.payment_kind,public.payment_method,uuid,bigint,text,text,text),
  public.start_desk_online_payment(public.payment_kind,uuid,bigint,text,text),
  public.confirm_online_payment(text,numeric,text) from public, anon, authenticated;
grant execute on function public.start_desk_online_payment(public.payment_kind,uuid,bigint,text,text) to authenticated;
grant execute on function public.confirm_online_payment(text,numeric,text) to service_role;
