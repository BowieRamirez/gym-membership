-- Item 13 of the 2026-11-12 plan, G22 (owner, 2026-11-15; design docs/superpowers/specs/2026-11-15-prorated-refunds-design.md).
-- Changes D9/D21: a membership refund is only the unused days, counted when staff approve (access ends then), and none once half the term
-- has passed. Asking before half is what counts: a request made in time can be approved after it.
--   used  = 0 before the start, else whole days since the start rounded up (a started day is used), at most the term's days
--   due   = what was paid x (days - used) / days, to the centavo
-- A request saves the estimate; approval sets the final amount. Requests approved or repaid before this keep their amounts (= the payment).
-- Same signatures, so create or replace keeps every grant.

create function public.term_days_used(p_starts timestamptz, p_total int, p_at timestamptz) returns int
language sql immutable set search_path = '' as $$
  select case when p_at <= p_starts then 0 else least(p_total, ceil(extract(epoch from p_at - p_starts) / 86400)::int) end;
$$;
revoke all on function public.term_days_used(timestamptz, int, timestamptz) from public, anon, authenticated;

create or replace function public.request_membership_refund(p_term_id bigint,p_reason text,p_category text default null) returns public.refund_requests
language plpgsql security definer set search_path='' as $$
declare v_term public.membership_terms; v_pay public.payments; v_request public.refund_requests; v_activated boolean; v_used int;
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
  v_used := public.term_days_used(v_term.starts_at, v_term.duration_days, now());
  if v_used * 2 >= v_term.duration_days then perform public.fail('refund_half_used','22023'); end if;   -- G22
  v_activated := exists(select 1 from public.gym_check_ins c where c.member_id=auth.uid()
    and c.checked_in_at>=v_term.starts_at and c.checked_in_at<v_term.ends_at);
  if v_activated and p_category is null then perform public.fail('refund_reason_required','22023'); end if;
  insert into public.refund_requests(term_id,payment_id,member_id,amount,reason,was_activated,review_reason)
    values(p_term_id,v_pay.payment_id,auth.uid(),round(v_pay.amount * (v_term.duration_days - v_used) / v_term.duration_days, 2),
           btrim(p_reason),v_activated,case when v_activated then p_category end) returning * into v_request;
  perform public.audit('refund.request','refund_requests',v_request.refund_request_id::text,
    jsonb_build_object('amount',v_request.amount,'paid',v_pay.amount,'days_used',v_used,'term_id',p_term_id,'activated',v_activated,'review_reason',v_request.review_reason));
  return v_request;
end $$;

create or replace function public.decide_membership_refund(p_request_id bigint,p_approve boolean,p_reason text default null) returns void
language plpgsql security definer set search_path='' as $$
declare v_request public.refund_requests; v_pay public.payments; v_term public.membership_terms; v_used int; v_due numeric;
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
  if v_pay.status<>'verified' or v_request.amount<=0 or v_request.amount>v_pay.amount then perform public.fail('not_refundable','P0002'); end if;
  if p_approve then
    select * into v_term from public.membership_terms where term_id=v_request.term_id;
    v_used := public.term_days_used(v_term.starts_at, v_term.duration_days, now());
    v_due := round(v_pay.amount * (v_term.duration_days - v_used) / v_term.duration_days, 2);   -- G22: the days left now, when access ends
    if v_due <= 0 then perform public.fail('nothing_to_refund','22023'); end if;
    update public.membership_terms set cancelled_at=now(),cancelled_by=auth.uid(),cancellation_reason='Refund approved'
      where term_id=v_request.term_id and cancelled_at is null;
    if not found then perform public.fail('already_processed','P0002'); end if;
    perform public.project_membership_terms(v_request.member_id);
  end if;
  update public.refund_requests set status=case when p_approve then 'approved' else 'rejected' end,
    amount=case when p_approve then v_due else amount end,
    decided_by=auth.uid(),decided_at=now(),decision_reason=btrim(p_reason) where refund_request_id=p_request_id;
  perform public.notify(v_request.member_id,'payment',case when p_approve then 'Cancellation approved; refund awaiting repayment' else 'Cancellation request rejected' end,
    case when p_approve then btrim(p_reason)||' · Refund PHP '||to_char(v_due,'FM999,999,990.00') else btrim(p_reason) end,
    jsonb_build_object('refund_request_id',p_request_id));
  perform public.audit('refund.decide','refund_requests',p_request_id::text,
    jsonb_build_object('approved',p_approve,'reason',btrim(p_reason),'amount',case when p_approve then v_due end,'days_used',v_used));
end $$;

-- the repayment is the approved amount (0 < amount <= the payment); 0055 left a bank_transfer test in here from 0025, now gone
create or replace function public.record_membership_refund_paid(p_request_id bigint,p_method public.payment_method,p_reference text default null) returns void
language plpgsql security definer set search_path='' as $$
declare v_request public.refund_requests; v_pay public.payments;
begin
  perform public.require_permission('payments:void');
  if p_method is null or p_method not in ('cash','gcash') or (p_method='gcash' and coalesce(btrim(p_reference),'')='') then
    perform public.fail('bad_input','22023');
  end if;
  select * into v_request from public.refund_requests where refund_request_id=p_request_id;
  if not found then perform public.fail('not_found','22023'); end if;
  if v_request.member_id=auth.uid() then perform public.fail('self_decision','42501'); end if;
  select * into v_pay from public.payments where payment_id=v_request.payment_id for update;
  select * into v_request from public.refund_requests where refund_request_id=p_request_id for update;
  if v_request.status<>'approved' then perform public.fail('already_processed','P0002'); end if;
  if v_pay.status<>'verified' or v_request.amount<=0 or v_request.amount>v_pay.amount then perform public.fail('not_refundable','P0002'); end if;
  update public.refund_requests set status='paid',paid_by=auth.uid(),paid_at=now(),repayment_method=p_method,
    repayment_reference=nullif(btrim(p_reference),'') where refund_request_id=p_request_id;
  perform public.notify(v_request.member_id,'payment','Refund repaid',p_method::text||' · PHP '||to_char(v_request.amount,'FM999,999,990.00'),
    jsonb_build_object('refund_request_id',p_request_id));
  perform public.audit('refund.paid','refund_requests',p_request_id::text,
    jsonb_build_object('amount',v_request.amount,'method',p_method,'reference',nullif(btrim(p_reference),'')));
end $$;
