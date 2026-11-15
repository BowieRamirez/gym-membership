-- G23 (owner, 2026-11-15, replaces G22's halfway cutoff): a membership refund can be asked for only within 7 days of the term's start
-- (a term bought ahead can be cancelled any time before it starts). The amount is unchanged from 0067: the unused days, counted when staff
-- approve, and a request made in time can still be approved after the 7 days. Same signature, so create or replace keeps the grants.

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
  if now() > v_term.starts_at + interval '7 days' then perform public.fail('refund_week_passed','22023'); end if;   -- G23
  v_used := public.term_days_used(v_term.starts_at, v_term.duration_days, now());
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
