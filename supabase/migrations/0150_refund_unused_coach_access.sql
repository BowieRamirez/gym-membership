-- Owner, 2026-11-25: approving a membership refund also refunds the unused days of the term's paid coach access (0129). It is its own
-- refund request ('coach_access', on the coach access payment), approved at once like the membership's, repaid with the same button and
-- counted against revenue like any refund. Unused days are worked out the way the membership's are (term_days_used, days started count),
-- over the days the coach access covered: from when it was bought to the end of the term.

alter table public.refund_requests drop constraint refund_requests_kind_check;
alter table public.refund_requests add constraint refund_requests_kind_check
  check (kind in ('membership', 'cancel', 'no_show', 'online', 'day_pass', 'walk_in_booking', 'coach_addon', 'coach_access'));

do $$
declare v_def text;
  v_old text := $q$    perform public.project_membership_terms(v_request.member_id);
  end if;$q$;
  v_new text := $q$    perform public.project_membership_terms(v_request.member_id);
    -- 0150: the unused days of paid coach access come back too
    select * into v_coach from public.payments where payment_id = v_term.coach_access_payment_id and status = 'verified' and amount > 0;
    if found and v_term.coach_access_at is not null then
      v_days := greatest(1, ceil(extract(epoch from v_term.ends_at - v_term.coach_access_at) / 86400)::int);
      v_coach_due := round(v_coach.amount * (v_days - public.term_days_used(v_term.coach_access_at, v_days, now())) / v_days, 2);
      if v_coach_due > 0 then
        insert into public.refund_requests (kind, payment_id, member_id, amount, reason, status, decided_by, decided_at, decision_reason)
        values ('coach_access', v_coach.payment_id, v_request.member_id, v_coach_due, 'Membership refunded', 'approved', auth.uid(), now(),
                'Unused coach access days, refunded with the membership')
        returning refund_request_id into v_coach_req;
        perform public.audit('refund.request', 'refund_requests', v_coach_req::text,
          jsonb_build_object('amount', v_coach_due, 'payment_id', v_coach.payment_id, 'kind', 'coach_access', 'term_id', v_term.term_id));
      end if;
    end if;
  end if;$q$;
begin
  v_def := pg_get_functiondef('public.decide_membership_refund(bigint,boolean,text)'::regprocedure);
  if array_length(string_to_array(v_def, v_old), 1) <> 2 then raise exception '0150: decide_membership_refund lacks the expected text'; end if;
  v_def := replace(v_def, v_old, v_new);
  v_def := replace(v_def, 'v_used int; v_due numeric;', 'v_used int; v_due numeric; v_coach public.payments; v_days int; v_coach_due numeric; v_coach_req bigint;');
  v_def := replace(v_def, $q$btrim(p_reason)||' · Refund PHP '||to_char(v_due,'FM999,999,990.00')$q$,
    $q$btrim(p_reason)||' · Refund PHP '||to_char(v_due,'FM999,999,990.00')||case when v_coach_due > 0 then ' + coach access PHP '||to_char(v_coach_due,'FM999,999,990.00') else '' end$q$);
  if position('v_coach_due numeric' in v_def) = 0 or position('+ coach access PHP' in v_def) = 0 then raise exception '0150: declarations or message not patched'; end if;
  execute v_def;
end $$;
