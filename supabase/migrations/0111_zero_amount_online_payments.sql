-- B3 / Q14(a) (2026-11-20): a ₱0 online payment (e.g. 100% discount code) is verified and activated at once without Xendit.

create or replace function public.with_package_method(p_pay public.payments, p_method public.payment_method) returns public.payments
language plpgsql security definer set search_path='' as $$
declare v public.payments;
begin
  if p_method = 'cash' then
    update public.payments set method = 'cash' where payment_id = p_pay.payment_id returning * into p_pay;
  end if;
  if p_pay.amount = 0 and p_pay.status = 'pending' then
    update public.payments
    set status = 'verified', verified_at = now(), receipt_no = public.next_receipt_no()
    where payment_id = p_pay.payment_id returning * into v;
    perform public.activate_payment_target(v.payment_id);
    perform public.notify(v.member_id, 'payment', 'Payment received', v.payment_code, jsonb_build_object('payment_id', v.payment_id));
    perform public.audit_as(v.member_id, 'payment.free', 'payments', v.payment_id::text, jsonb_build_object('discount_id', v.discount_id));
    return v;
  end if;
  return p_pay;
end $$;

revoke all on function public.with_package_method(public.payments, public.payment_method) from public, anon, authenticated;

do $$
declare
  v_def text := pg_get_functiondef('public.book_amenity_for(uuid,boolean,bigint,timestamptz,integer,public.payment_method,uuid,text)'::regprocedure);
  v_old text := $q$  perform public.audit('reservation.request', 'reservations', v_res.reservation_id::text,
    jsonb_build_object('amenity', p_amenity, 'starts_at', p_start, 'ends_at', v_end, 'payment_id', v_pay.payment_id, 'method', p_method,
                       'coach', p_coach, 'total', v_due, 'down_payment', v_down, 'discount', v_disc));
  return v_res;$q$;
  v_new text := $q$  perform public.audit('reservation.request', 'reservations', v_res.reservation_id::text,
    jsonb_build_object('amenity', p_amenity, 'starts_at', p_start, 'ends_at', v_end, 'payment_id', v_pay.payment_id, 'method', p_method,
                       'coach', p_coach, 'total', v_due, 'down_payment', v_down, 'discount', v_disc));
  if v_down = 0 then
    update public.payments
    set status = 'verified', verified_at = now(), receipt_no = public.next_receipt_no()
    where payment_id = v_pay.payment_id;
    perform public.activate_payment_target(v_pay.payment_id);
    perform public.notify(v_uid, 'payment', 'Payment received', v_pay.payment_code, jsonb_build_object('payment_id', v_pay.payment_id));
    perform public.audit_as(v_uid, 'payment.free', 'payments', v_pay.payment_id::text, jsonb_build_object('discount_id', v_disc));
    select * into v_res from public.reservations where reservation_id = v_res.reservation_id;
  end if;
  return v_res;$q$;
begin
  if array_length(string_to_array(v_def, v_old), 1) <> 2 then
    raise exception '0111: book_amenity_for lacks reservation.request block';
  end if;
  execute replace(v_def, v_old, v_new);
end $$;

revoke all on function public.book_amenity_for(uuid, boolean, bigint, timestamptz, integer, public.payment_method, uuid, text) from public, anon, authenticated;
