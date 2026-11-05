-- Item R2 (2026-11-05, owner decisions D9, D10): a member gets a full refund if the term has not been activated; otherwise staff
-- review the request. This is shaped by guidance on Philippine consumer rules and is not legal advice.
-- Activated means the term has started. A check-in during a term can only happen after it starts, so it adds nothing to that test.
-- The 24-hour window from 0018 is gone: a request is accepted until the term ends. Approving still cancels only this term (item 11).

alter table public.refund_requests
  add column was_activated boolean,   -- null: asked before this rule existed
  add column review_reason text check (review_reason in ('service_not_provided','misled','contract_allows','illness_or_relocation','change_of_mind')),
  add constraint refund_review_needs_reason check (was_activated is distinct from true or review_reason is not null);

-- p_category defaults to null so an installed app calling with two arguments still reaches this function
drop function public.request_membership_refund(bigint,text);
create function public.request_membership_refund(p_term_id bigint,p_reason text,p_category text default null) returns public.refund_requests
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
  v_activated := now()>=v_term.starts_at;
  if v_activated and p_category is null then perform public.fail('refund_reason_required','22023'); end if;
  insert into public.refund_requests(term_id,payment_id,member_id,amount,reason,was_activated,review_reason)
    values(p_term_id,v_pay.payment_id,auth.uid(),v_pay.amount,btrim(p_reason),v_activated,case when v_activated then p_category end) returning * into v_request;
  perform public.audit('refund.request','refund_requests',v_request.refund_request_id::text,
    jsonb_build_object('amount',v_pay.amount,'term_id',p_term_id,'activated',v_activated,'review_reason',v_request.review_reason));
  return v_request;
end $$;
revoke all on function public.request_membership_refund(bigint,text,text) from public, anon;
grant execute on function public.request_membership_refund(bigint,text,text) to authenticated;
