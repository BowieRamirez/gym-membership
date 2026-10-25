-- D3 (2026-10-22): refund decisions and repayments need the same payments:void grant as voiding, not just the admin role.
-- Bodies are unchanged from 0018 apart from that check; create or replace keeps the existing execute grants.

create or replace function public.decide_membership_refund(p_request_id bigint,p_approve boolean,p_reason text default null) returns void
language plpgsql security definer set search_path='' as $$
declare v_request public.refund_requests; v_pay public.payments;
begin
  perform public.require_permission('payments:void');
  if p_approve is null or length(btrim(coalesce(p_reason,''))) not between 1 and 1000 then perform public.fail('bad_input','22023'); end if;
  select * into v_request from public.refund_requests where refund_request_id=p_request_id;
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

create or replace function public.record_membership_refund_paid(p_request_id bigint,p_method public.payment_method,p_reference text default null) returns void
language plpgsql security definer set search_path='' as $$
declare v_request public.refund_requests; v_pay public.payments;
begin
  perform public.require_permission('payments:void');
  if p_method is null or p_method='qr' or (p_method in ('gcash','bank_transfer') and coalesce(btrim(p_reference),'')='') then
    perform public.fail('bad_input','22023');
  end if;
  select * into v_request from public.refund_requests where refund_request_id=p_request_id;
  if not found then perform public.fail('not_found','22023'); end if;
  if v_request.member_id=auth.uid() then perform public.fail('self_decision','42501'); end if;
  select * into v_pay from public.payments where payment_id=v_request.payment_id for update;
  select * into v_request from public.refund_requests where refund_request_id=p_request_id for update;
  if v_request.status<>'approved' then perform public.fail('already_processed','P0002'); end if;
  if v_pay.status<>'verified' or v_request.amount<>v_pay.amount then perform public.fail('not_refundable','P0002'); end if;
  update public.refund_requests set status='paid',paid_by=auth.uid(),paid_at=now(),repayment_method=p_method,
    repayment_reference=nullif(btrim(p_reference),'') where refund_request_id=p_request_id;
  perform public.notify(v_request.member_id,'payment','Refund repaid',p_method::text,jsonb_build_object('refund_request_id',p_request_id));
  perform public.audit('refund.paid','refund_requests',p_request_id::text,
    jsonb_build_object('amount',v_request.amount,'method',p_method,'reference',nullif(btrim(p_reference),'')));
end $$;
