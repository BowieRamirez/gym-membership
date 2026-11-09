-- Phase 3 of the 2026-11-09 plan (owner decisions F1, F2, F3): the cash-drawer shift.
--
-- Each cashier (front desk, Admin, System admin: whoever records counter payments) opens their own shift with the counted float;
-- a float other than the gym's opening float needs a note. While it is open, every cash payment they take and every cash refund
-- they pay out is tied to it, and cash can't be taken without one (no_open_shift). Closing stores cash in, cash out, expected
-- (float + in - out) and the count; a count that differs needs a note. Shifts are written only by these functions, so a closed
-- shift never changes. Admin and System admin list every shift (Revenue page).
--
-- The tie is made by triggers, not in each payment function, so every path that takes cash (counter payments, verifying a
-- pending cash payment, paying a refund in cash) is covered. Writes with no signed-in user (jobs, seeding) are left alone.

alter table public.gym_settings add column opening_float numeric(12,2) not null default 5000 check (opening_float >= 0);

create table public.cash_shifts (
  shift_id bigint generated always as identity primary key,
  cashier_id uuid not null references public.profiles,
  opened_at timestamptz not null default now(),
  opening_float numeric(12,2) not null check (opening_float >= 0),
  open_note text check (length(open_note) between 1 and 500),
  closed_at timestamptz,
  cash_in numeric(12,2),
  cash_out numeric(12,2),
  expected numeric(12,2),
  counted numeric(12,2) check (counted >= 0),
  close_note text check (length(close_note) between 1 and 500),
  check ((closed_at is null and cash_in is null and cash_out is null and expected is null and counted is null and close_note is null)
      or (closed_at is not null and cash_in is not null and cash_out is not null and expected is not null and counted is not null))
);
create unique index cash_shifts_one_open on public.cash_shifts(cashier_id) where closed_at is null;
create index cash_shifts_opened on public.cash_shifts(opened_at);
alter table public.cash_shifts enable row level security;   -- no grants or policies: read and written only through the functions below
comment on table public.cash_shifts is 'Cash-drawer shifts: one open shift per cashier; closed shifts are read-only (0050)';

alter table public.payments add column cash_shift_id bigint references public.cash_shifts;
alter table public.refund_requests add column cash_shift_id bigint references public.cash_shifts;
create index payments_cash_shift on public.payments(cash_shift_id) where cash_shift_id is not null;
create index refund_requests_cash_shift on public.refund_requests(cash_shift_id) where cash_shift_id is not null;

create function public.tie_cash_to_shift() returns trigger
language plpgsql security definer set search_path='' as $$
declare v_taken boolean; v_by uuid;
begin
  if auth.uid() is null then return new; end if;
  if tg_table_name = 'payments' then
    v_taken := new.status = 'verified' and new.method = 'cash' and (tg_op = 'INSERT' or old.status <> 'verified');
    v_by := new.verified_by;
  else
    v_taken := new.status = 'paid' and new.repayment_method = 'cash' and old.status <> 'paid';
    v_by := new.paid_by;
  end if;
  if not v_taken then return new; end if;
  select shift_id into new.cash_shift_id from public.cash_shifts where cashier_id = coalesce(v_by, auth.uid()) and closed_at is null;
  if new.cash_shift_id is null then raise exception 'no_open_shift' using errcode = 'P0002'; end if;
  return new;
end $$;
revoke all on function public.tie_cash_to_shift() from public, anon, authenticated;
create trigger payments_cash_shift before insert or update of status on public.payments
  for each row execute function public.tie_cash_to_shift();
create trigger refund_requests_cash_shift before update of status on public.refund_requests
  for each row execute function public.tie_cash_to_shift();

-- cash in and out of a shift as it stands now (voided payments drop out, as in daily_cash_summary)
create function public.cash_shift_totals(p_shift bigint, out cash_in numeric, out cash_out numeric)
language sql stable security definer set search_path='' as $$
  select coalesce((select sum(amount) from public.payments where cash_shift_id = p_shift and status = 'verified'), 0),
         coalesce((select sum(amount) from public.refund_requests where cash_shift_id = p_shift and status = 'paid'), 0);
$$;
revoke all on function public.cash_shift_totals(bigint) from public, anon, authenticated;

create function public.open_cash_shift(p_float numeric, p_note text default null) returns public.cash_shifts
language plpgsql security definer set search_path='' as $$
declare v_shift public.cash_shifts; v_note text := nullif(btrim(coalesce(p_note, '')), '');
begin
  perform public.require_permission('payments:record');
  if p_float is null or p_float < 0 or p_float > 10000000 or length(coalesce(v_note, '')) > 500 then perform public.fail('bad_input', '22023'); end if;
  if p_float <> (select opening_float from public.gym_settings where id = 1) and v_note is null then perform public.fail('note_required', '22023'); end if;
  begin
    insert into public.cash_shifts(cashier_id, opening_float, open_note) values (auth.uid(), round(p_float, 2), v_note) returning * into v_shift;
  exception when unique_violation then perform public.fail('shift_open', 'P0002');
  end;
  perform public.audit('cash_shift.open', 'cash_shifts', v_shift.shift_id::text, jsonb_build_object('float', v_shift.opening_float, 'note', v_note));
  return v_shift;
end $$;

create function public.close_cash_shift(p_counted numeric, p_note text default null) returns public.cash_shifts
language plpgsql security definer set search_path='' as $$
declare v_shift public.cash_shifts; v_note text := nullif(btrim(coalesce(p_note, '')), ''); v_in numeric; v_out numeric;
begin
  perform public.require_permission('payments:record');
  if p_counted is null or p_counted < 0 or p_counted > 10000000 or length(coalesce(v_note, '')) > 500 then perform public.fail('bad_input', '22023'); end if;
  select * into v_shift from public.cash_shifts where cashier_id = auth.uid() and closed_at is null for update;
  if not found then perform public.fail('no_open_shift', 'P0002'); end if;
  select t.cash_in, t.cash_out into v_in, v_out from public.cash_shift_totals(v_shift.shift_id) t;
  if round(p_counted, 2) <> v_shift.opening_float + v_in - v_out and v_note is null then perform public.fail('note_required', '22023'); end if;
  update public.cash_shifts set closed_at = now(), cash_in = v_in, cash_out = v_out, expected = opening_float + v_in - v_out,
    counted = round(p_counted, 2), close_note = v_note where shift_id = v_shift.shift_id returning * into v_shift;
  perform public.audit('cash_shift.close', 'cash_shifts', v_shift.shift_id::text,
    jsonb_build_object('expected', v_shift.expected, 'counted', v_shift.counted, 'note', v_note));
  return v_shift;
end $$;

-- the caller's open shift with its running totals, or no row
create function public.my_cash_shift() returns table(shift_id bigint, opened_at timestamptz, opening_float numeric, cash_in numeric, cash_out numeric, expected numeric)
language plpgsql stable security definer set search_path='' as $$
begin
  perform public.require_permission('payments:record');
  return query select s.shift_id, s.opened_at, s.opening_float, t.cash_in, t.cash_out, s.opening_float + t.cash_in - t.cash_out
    from public.cash_shifts s cross join lateral public.cash_shift_totals(s.shift_id) t
    where s.cashier_id = auth.uid() and s.closed_at is null;
end $$;

-- every shift opened in a range of Manila days, newest first; open shifts show live totals and no count
create function public.cash_shifts_list(p_from date, p_to date)
returns table(shift_id bigint, cashier text, opened_at timestamptz, closed_at timestamptz, opening_float numeric, cash_in numeric,
              cash_out numeric, expected numeric, counted numeric, difference numeric, open_note text, close_note text)
language plpgsql stable security definer set search_path='' as $$
begin
  perform public.require_permission('reports:revenue');
  if p_from is null or p_to is null or p_from > p_to then perform public.fail('bad_input', '22023'); end if;
  return query select s.shift_id, p.full_name, s.opened_at, s.closed_at, s.opening_float,
      coalesce(s.cash_in, t.cash_in), coalesce(s.cash_out, t.cash_out), coalesce(s.expected, s.opening_float + t.cash_in - t.cash_out),
      s.counted, s.counted - s.expected, s.open_note, s.close_note
    from public.cash_shifts s join public.profiles p on p.id = s.cashier_id cross join lateral public.cash_shift_totals(s.shift_id) t
    where (s.opened_at at time zone 'Asia/Manila')::date between p_from and p_to
    order by s.opened_at desc;
end $$;

revoke all on function public.open_cash_shift(numeric, text), public.close_cash_shift(numeric, text), public.my_cash_shift(),
  public.cash_shifts_list(date, date) from public, anon, authenticated;
grant execute on function public.open_cash_shift(numeric, text), public.close_cash_shift(numeric, text), public.my_cash_shift(),
  public.cash_shifts_list(date, date) to authenticated;
