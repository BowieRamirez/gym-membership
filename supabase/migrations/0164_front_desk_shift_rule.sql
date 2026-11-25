-- 0164 (owner, 2026-11-25): the front desk works only inside their shift.
-- 1. Starting a shift (open_cash_shift) always follows the front desk person's schedule, whatever the Scheduling switch says
--    (the switch now only governs coach check-in): refused on a gym closure day (day_closed), outside gym hours
--    (gym_closed; from 5 minutes before opening), with no shift today (no_shift_today), more than 5 minutes before the shift
--    starts (too_early:<start>) or after it ended (shift_over). Server time. Admins and System admins are not limited (S1).
-- 2. Nothing is sold without a started shift: a payment (cash or online), a booking or a coach session the front desk makes is
--    refused no_open_shift until they start their shift (the create-user Edge Function refuses new accounts the same way).
-- 3. my_shift_window() tells the Cashier when the Start shift button opens, for its countdown.

create function public.is_desk_only() returns boolean
language sql stable security definer set search_path = '' as $$
  select public.has_role('employee') and not (public.has_role('admin') or public.has_role('subadmin'));
$$;
revoke all on function public.is_desk_only() from public, anon, authenticated;

create function public.desk_clock_in(out o_start timestamptz, out o_end timestamptz)
language plpgsql stable security definer set search_path = '' as $$
declare v_now timestamptz := now(); v_time time := (now() at time zone 'Asia/Manila')::time; v_open time; v_close time;
begin
  if public.day_is_closed(v_now) then perform public.fail('day_closed', '22023'); end if;
  select opens_at, closes_at into v_open, v_close from public.gym_settings where id = 1;
  if (v_time < v_open and v_open - v_time > interval '5 minutes') or v_time >= v_close then perform public.fail('gym_closed', '22023'); end if;
  select f.starts_at, f.ends_at into o_start, o_end from public.shift_for(auth.uid(), (v_now at time zone 'Asia/Manila')::date) f;
  if o_start is null then perform public.fail('no_shift_today', '22023'); end if;
  if v_now < o_start - interval '5 minutes' then
    perform public.fail('too_early:' || to_char(o_start at time zone 'Asia/Manila', 'FMHH12:MI AM'), '22023');
  end if;
  if v_now >= o_end then perform public.fail('shift_over', '22023'); end if;
end $$;
revoke all on function public.desk_clock_in() from public, anon, authenticated;

-- 0151 as it was, with the front desk's rule always on
create or replace function public.open_cash_shift(p_float numeric, p_note text default null) returns public.cash_shifts
language plpgsql security definer set search_path = '' as $$
declare v_shift public.cash_shifts; v_note text := nullif(btrim(coalesce(p_note, '')), ''); v_s timestamptz; v_e timestamptz;
begin
  perform public.require_permission('payments:record');
  if p_float is null or p_float < 0 or p_float > 10000000 or length(coalesce(v_note, '')) > 500 then perform public.fail('bad_input', '22023'); end if;
  if p_float <> (select opening_float from public.gym_settings where id = 1) and v_note is null then perform public.fail('note_required', '22023'); end if;
  if exists (select 1 from public.cash_shifts where cashier_id = auth.uid() and closed_at is null) then perform public.fail('shift_open', 'P0002'); end if;
  if public.is_desk_only() then
    select o_start, o_end into v_s, v_e from public.desk_clock_in();
  end if;
  begin
    insert into public.cash_shifts(cashier_id, opening_float, open_note, expected_start, expected_end)
    values (auth.uid(), round(p_float, 2), v_note, v_s, v_e) returning * into v_shift;
  exception when unique_violation then perform public.fail('shift_open', 'P0002');
  end;
  perform public.audit('cash_shift.open', 'cash_shifts', v_shift.shift_id::text, jsonb_build_object('float', v_shift.opening_float, 'note', v_note));
  return v_shift;
end $$;
revoke all on function public.open_cash_shift(numeric, text) from public, anon;
grant execute on function public.open_cash_shift(numeric, text) to authenticated;

create function public.desk_needs_open_shift() returns trigger
language plpgsql security definer set search_path = '' as $$
begin
  if auth.uid() is null or not public.is_desk_only() then return new; end if;
  if not exists (select 1 from public.cash_shifts where cashier_id = auth.uid() and closed_at is null) then
    perform public.fail('no_open_shift', 'P0002');
  end if;
  return new;
end $$;
revoke all on function public.desk_needs_open_shift() from public, anon, authenticated;
create trigger a_desk_needs_open_shift before insert on public.payments for each row execute function public.desk_needs_open_shift();
create trigger a_desk_needs_open_shift before insert on public.reservations for each row execute function public.desk_needs_open_shift();
create trigger a_desk_needs_open_shift before insert on public.training_sessions for each row execute function public.desk_needs_open_shift();

-- the Cashier's countdown: when this person may start their shift, in server time
create function public.my_shift_window() returns jsonb
language plpgsql stable security definer set search_path = '' as $$
declare v_now timestamptz := now(); v_day date := (now() at time zone 'Asia/Manila')::date; v_s timestamptz; v_e timestamptz; v_g record;
begin
  perform public.require_permission('payments:record');
  if not public.is_desk_only() then return jsonb_build_object('now', v_now, 'limited', false); end if;
  select f.starts_at, f.ends_at into v_s, v_e from public.shift_for(auth.uid(), v_day) f;
  select opens_at, closes_at into v_g from public.gym_settings where id = 1;
  return jsonb_build_object('now', v_now, 'limited', true, 'starts_at', v_s, 'ends_at', v_e,
    'closed_today', public.day_is_closed(v_now),
    'gym_opens', (v_day + v_g.opens_at) at time zone 'Asia/Manila', 'gym_closes', (v_day + v_g.closes_at) at time zone 'Asia/Manila');
end $$;
revoke all on function public.my_shift_window() from public, anon;
grant execute on function public.my_shift_window() to authenticated;
