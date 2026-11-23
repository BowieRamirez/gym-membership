-- Fix B2: walk-in coach sessions (member_id is null) must not be cancellable by unrelated members or without a coach reason.
-- Fix B3: damage_reports may only be written via report_damage / review_damage (revoke direct insert, update).

create or replace function public.cancel_session(p_session_id bigint, p_reason text default null) returns void
language plpgsql security definer
set search_path = ''
as $$
declare
  v_uid uuid := auth.uid();
  v_s public.training_sessions;
  v_pay public.payments;
  v_mine boolean;
  v_refund boolean := false;
  v_req bigint;
begin
  perform public.require_active_user();
  select * into v_s from public.training_sessions where training_session_id = p_session_id for update;
  if not found or (v_s.coach_id is distinct from v_uid and v_s.member_id is distinct from v_uid and not public.is_staff()) then
    perform public.fail('not_found', '22023');
  end if;
  if v_s.status <> 'scheduled' then perform public.fail('already_processed', 'P0002'); end if;
  if v_s.scheduled_start <= now() then perform public.fail('already_started', 'P0002'); end if;
  if v_uid = v_s.coach_id and v_uid is distinct from v_s.member_id and not public.is_staff() and nullif(btrim(p_reason), '') is null then
    perform public.fail('reason_required', '22023');
  end if;

  v_mine := (v_s.member_id is not distinct from v_uid);
  update public.training_sessions
    set status = 'cancelled', cancelled_at = now(), cancelled_by = v_uid,
        cancel_reason = nullif(btrim(p_reason), ''),
        notes = coalesce(nullif(btrim(p_reason), ''), notes)
    where training_session_id = p_session_id;

  update public.time_requests set status = 'cancelled' where training_session_id = p_session_id and status = 'pending';
  update public.reservations set status = 'cancelled', cancelled_at = now(), cancelled_by = v_uid,
    cancel_reason = coalesce(nullif(btrim(p_reason), ''), 'Session cancelled')
    where training_session_id = p_session_id and status = 'confirmed';

  if v_s.payment_id is not null then
    select * into v_pay from public.payments where payment_id = v_s.payment_id for update;
    if v_pay.status = 'pending' then
      update public.payments set status = 'rejected', rejected_reason = 'Session cancelled' where payment_id = v_pay.payment_id;
    elsif v_pay.status = 'verified' and coalesce(v_pay.amount, 0) > 0 then
      v_refund := (not v_mine) or (v_s.scheduled_start - interval '24 hours' >= now());
      if v_refund then
        insert into public.refund_requests (kind, training_session_id, payment_id, member_id, amount, reason, status, decided_by, decided_at, decision_reason)
        values ('coach_addon', p_session_id, v_pay.payment_id, v_s.member_id, v_pay.amount,
                coalesce(nullif(btrim(p_reason), ''), 'Coach session cancelled'),
                'approved', v_uid, now(),
                case when v_mine then 'Cancelled 24 hours or more before the start: coach add-on refunded'
                     else 'Cancelled by the coach or gym: coach add-on refunded' end)
        returning refund_request_id into v_req;
        perform public.notify_staff('payment', 'Coach add-on refund to repay', v_pay.payment_code, jsonb_build_object('refund_request_id', v_req));
      end if;
    end if;
  end if;

  if v_s.member_id is not null and v_uid is distinct from v_s.member_id then
    perform public.notify(v_s.member_id, 'system', 'Your coach session was cancelled', nullif(btrim(p_reason), ''), jsonb_build_object('training_session_id', p_session_id));
  end if;
  if v_uid is distinct from v_s.coach_id then
    perform public.notify(v_s.coach_id, 'system', 'A training session was cancelled', nullif(btrim(p_reason), ''), jsonb_build_object('training_session_id', p_session_id));
  end if;
  perform public.audit('coach_session.cancel', 'training_sessions', p_session_id::text, jsonb_build_object('reason', nullif(btrim(p_reason), ''), 'refund', v_refund));
end $$;

revoke all on function public.cancel_session(bigint, text) from public, anon;
grant execute on function public.cancel_session(bigint, text) to authenticated;

revoke insert, update on public.damage_reports from authenticated;
drop policy if exists damage_reports_insert on public.damage_reports;
drop policy if exists damage_reports_update on public.damage_reports;
