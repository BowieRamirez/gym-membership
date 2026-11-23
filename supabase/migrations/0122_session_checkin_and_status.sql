-- Phase 12 (E9): Session QR check-in, Coach is here, session status apart from payment.

alter table public.training_sessions
  add column if not exists session_code text,
  add column if not exists checked_in_at timestamptz,
  add column if not exists coach_arrived_at timestamptz;

update public.training_sessions
set session_code = 'SES-' || upper(substr(md5(training_session_id::text || '-' || gen_random_uuid()::text), 1, 6))
where session_code is null;

alter table public.training_sessions
  alter column session_code set default ('SES-' || upper(substr(md5(gen_random_uuid()::text), 1, 6)));

create unique index if not exists training_sessions_session_code_idx
  on public.training_sessions (upper(session_code))
  where session_code is not null;

-- Desk scans a session code/QR: checks current status, refuses cancelled/unpaid/wrong day with reason, records check-in
create or replace function public.scan_session(p_code text)
returns table (
  training_session_id bigint,
  member_id uuid,
  member_name text,
  coach_id uuid,
  coach_name text,
  scheduled_start timestamptz,
  scheduled_end timestamptz,
  checked_in_at timestamptz,
  coach_arrived_at timestamptz,
  status text,
  session_code text
)
language plpgsql security definer
set search_path = ''
as $$
declare
  v_s public.training_sessions;
  v_checked timestamptz;
begin
  perform public.require_active_user();
  if not public.is_staff() then perform public.fail('forbidden', '42501'); end if;

  select * into v_s
  from public.training_sessions ts
  where ts.session_code is not null and upper(ts.session_code) = upper(btrim(coalesce(p_code, '')))
  for update;

  if not found then perform public.fail('not_found', 'P0002'); end if;
  if v_s.status = 'cancelled' then perform public.fail('session_cancelled', 'P0002'); end if;
  if v_s.status in ('completed', 'no_show') then perform public.fail('already_processed', 'P0002'); end if;
  if v_s.held_until is not null then perform public.fail('payment_required', 'P0002'); end if;
  if (v_s.scheduled_start at time zone 'Asia/Manila')::date <> (now() at time zone 'Asia/Manila')::date then
    perform public.fail('wrong_day', 'P0002');
  end if;

  if v_s.checked_in_at is null then
    update public.training_sessions ts
    set checked_in_at = now()
    where ts.training_session_id = v_s.training_session_id
    returning ts.checked_in_at into v_checked;
    perform public.audit('coach_session.check_in', 'training_sessions', v_s.training_session_id::text,
      jsonb_build_object('member_id', v_s.member_id, 'coach_id', v_s.coach_id));
  else
    v_checked := v_s.checked_in_at;
  end if;

  return query
  select v_s.training_session_id,
         v_s.member_id,
         (select p.full_name from public.profiles p where p.id = v_s.member_id),
         v_s.coach_id,
         (select p.full_name from public.profiles p where p.id = v_s.coach_id),
         v_s.scheduled_start,
         v_s.scheduled_end,
         v_checked,
         v_s.coach_arrived_at,
         case
           when v_s.coach_arrived_at is not null then 'In session'
           when v_checked is not null then 'Checked in'
           else v_s.status::text
         end,
         v_s.session_code;
end $$;

-- Desk taps "Coach is here" after member check-in: transitions session to In session
create or replace function public.mark_coach_arrived(p_session bigint) returns void
language plpgsql security definer
set search_path = ''
as $$
declare v_s public.training_sessions;
begin
  perform public.require_active_user();
  if not public.is_staff() then perform public.fail('forbidden', '42501'); end if;

  select * into v_s from public.training_sessions where training_session_id = p_session for update;
  if not found then perform public.fail('not_found', 'P0002'); end if;
  if v_s.status <> 'scheduled' then perform public.fail('already_processed', 'P0002'); end if;
  if v_s.held_until is not null then perform public.fail('payment_required', 'P0002'); end if;
  if v_s.checked_in_at is null then perform public.fail('not_checked_in', 'P0002'); end if;

  if v_s.coach_arrived_at is null then
    update public.training_sessions set coach_arrived_at = now() where training_session_id = p_session;
    perform public.audit('coach_session.coach_arrived', 'training_sessions', p_session::text,
      jsonb_build_object('coach_id', v_s.coach_id, 'member_id', v_s.member_id));
  end if;
end $$;

-- Today's coach sessions for desk Home & Membership checking
create or replace function public.today_coach_sessions()
returns table (
  training_session_id bigint,
  session_code text,
  member_id uuid,
  member_name text,
  coach_id uuid,
  coach_name text,
  scheduled_start timestamptz,
  scheduled_end timestamptz,
  status text,
  checked_in_at timestamptz,
  coach_arrived_at timestamptz,
  held_until timestamptz
)
language plpgsql stable security definer
set search_path = ''
as $$
declare v_today date := (now() at time zone 'Asia/Manila')::date;
begin
  perform public.require_active_user();
  if not public.is_staff() then perform public.fail('forbidden', '42501'); end if;

  return query
  select ts.training_session_id,
         ts.session_code,
         ts.member_id,
         mp.full_name,
         ts.coach_id,
         cp.full_name,
         ts.scheduled_start,
         ts.scheduled_end,
         case
           when ts.held_until is not null then 'Waiting for payment'
           when ts.status = 'scheduled' and ts.coach_arrived_at is not null then 'In session'
           when ts.status = 'scheduled' and ts.checked_in_at is not null then 'Checked in'
           else ts.status::text
         end,
         ts.checked_in_at,
         ts.coach_arrived_at,
         ts.held_until
  from public.training_sessions ts
  join public.profiles mp on mp.id = ts.member_id
  join public.profiles cp on cp.id = ts.coach_id
  where ts.status <> 'cancelled'
    and (ts.held_until is null or ts.held_until > now())
    and (ts.scheduled_start at time zone 'Asia/Manila')::date = v_today
  order by ts.scheduled_start;
end $$;

-- Coach cancelling a session requires a reason; alerts member and refunds paid add-on
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
  if not found or (v_s.coach_id <> v_uid and v_s.member_id <> v_uid and not public.is_staff()) then
    perform public.fail('not_found', '22023');
  end if;
  if v_s.status <> 'scheduled' then perform public.fail('already_processed', 'P0002'); end if;
  if v_s.scheduled_start <= now() then perform public.fail('already_started', 'P0002'); end if;
  if v_uid = v_s.coach_id and v_uid <> v_s.member_id and not public.is_staff() and nullif(btrim(p_reason), '') is null then
    perform public.fail('reason_required', '22023');
  end if;

  v_mine := (v_s.member_id = v_uid);
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

  if v_uid <> v_s.member_id then
    perform public.notify(v_s.member_id, 'system', 'Your coach session was cancelled', nullif(btrim(p_reason), ''), jsonb_build_object('training_session_id', p_session_id));
  end if;
  if v_uid <> v_s.coach_id then
    perform public.notify(v_s.coach_id, 'system', 'A training session was cancelled', nullif(btrim(p_reason), ''), jsonb_build_object('training_session_id', p_session_id));
  end if;
  perform public.audit('coach_session.cancel', 'training_sessions', p_session_id::text, jsonb_build_object('reason', nullif(btrim(p_reason), ''), 'refund', v_refund));
end $$;

revoke all on function
  public.scan_session(text),
  public.mark_coach_arrived(bigint),
  public.today_coach_sessions(),
  public.cancel_session(bigint, text)
from public, anon, authenticated;

grant execute on function
  public.scan_session(text),
  public.mark_coach_arrived(bigint),
  public.today_coach_sessions(),
  public.cancel_session(bigint, text)
to authenticated;
