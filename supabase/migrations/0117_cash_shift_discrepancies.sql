-- Phase 6 (D7, D8, 2026-11-20): cash shift close reason, shortage/overage figures, and append-only supervisor investigation notes.

alter table public.cash_shifts
  add column close_reason text check (close_reason in ('wrong_change', 'unrecorded_sale', 'counting_mistake', 'float_wrong', 'other')),
  add column resolved_at timestamptz;

create table public.cash_shift_notes (
  note_id bigint generated always as identity primary key,
  shift_id bigint not null references public.cash_shifts,
  author_id uuid not null references public.profiles,
  note text not null check (length(btrim(note)) between 1 and 1000),
  resolved boolean not null default false,
  created_at timestamptz not null default now()
);
create index cash_shift_notes_shift on public.cash_shift_notes(shift_id, created_at, note_id);
alter table public.cash_shift_notes enable row level security;   -- no direct grants: append-only via add_cash_shift_note
comment on table public.cash_shift_notes is 'Append-only supervisor investigation notes on closed cash shifts (0117)';

drop function public.close_cash_shift(numeric, text);
create function public.close_cash_shift(p_counted numeric, p_note text default null, p_reason text default null) returns public.cash_shifts
language plpgsql security definer set search_path='' as $$
declare
  v_shift public.cash_shifts;
  v_note text := nullif(btrim(coalesce(p_note, '')), '');
  v_reason text := nullif(btrim(coalesce(p_reason, '')), '');
  v_in numeric;
  v_out numeric;
  v_expected numeric;
  v_counted numeric;
begin
  perform public.require_permission('payments:record');
  if p_counted is null or p_counted < 0 or p_counted > 10000000 or length(coalesce(v_note, '')) > 500 then
    perform public.fail('bad_input', '22023');
  end if;
  select * into v_shift from public.cash_shifts where cashier_id = auth.uid() and closed_at is null for update;
  if not found then perform public.fail('no_open_shift', 'P0002'); end if;
  select t.cash_in, t.cash_out into v_in, v_out from public.cash_shift_totals(v_shift.shift_id) t;
  v_expected := v_shift.opening_float + v_in - v_out;
  v_counted := round(p_counted, 2);
  if v_counted <> v_expected then
    if v_reason is null then perform public.fail('reason_required', '22023'); end if;
    if v_reason not in ('wrong_change', 'unrecorded_sale', 'counting_mistake', 'float_wrong', 'other') then
      perform public.fail('bad_input', '22023');
    end if;
    if v_reason = 'other' and v_note is null then perform public.fail('note_required', '22023'); end if;
  else
    v_reason := null;
  end if;
  update public.cash_shifts
     set closed_at = now(), cash_in = v_in, cash_out = v_out, expected = v_expected,
         counted = v_counted, close_note = v_note, close_reason = v_reason
   where shift_id = v_shift.shift_id
   returning * into v_shift;
  perform public.audit('cash_shift.close', 'cash_shifts', v_shift.shift_id::text,
    jsonb_build_object('expected', v_shift.expected, 'counted', v_shift.counted, 'reason', v_reason, 'note', v_note));
  return v_shift;
end $$;

create function public.add_cash_shift_note(p_shift bigint, p_note text, p_resolve boolean default false) returns public.cash_shift_notes
language plpgsql security definer set search_path='' as $$
declare
  v_shift public.cash_shifts;
  v_note text := nullif(btrim(coalesce(p_note, '')), '');
  v_row public.cash_shift_notes;
begin
  perform public.require_permission('reports:revenue');
  if p_shift is null or v_note is null or length(v_note) > 1000 then perform public.fail('bad_input', '22023'); end if;
  select * into v_shift from public.cash_shifts where shift_id = p_shift for update;
  if not found or v_shift.closed_at is null then perform public.fail('bad_input', '22023'); end if;
  insert into public.cash_shift_notes(shift_id, author_id, note, resolved)
  values (p_shift, auth.uid(), v_note, coalesce(p_resolve, false))
  returning * into v_row;
  if coalesce(p_resolve, false) and v_shift.resolved_at is null then
    update public.cash_shifts set resolved_at = now() where shift_id = p_shift;
  end if;
  perform public.audit('cash_shift.note', 'cash_shifts', p_shift::text,
    jsonb_build_object('note', v_note, 'resolved', coalesce(p_resolve, false)));
  return v_row;
end $$;

drop function public.cash_shifts_list(date, date);
create function public.cash_shifts_list(p_from date, p_to date)
returns table(shift_id bigint, cashier text, opened_at timestamptz, closed_at timestamptz, opening_float numeric, cash_in numeric,
              cash_out numeric, expected numeric, counted numeric, difference numeric, shortage numeric, overage numeric,
              open_note text, close_note text, close_reason text, resolved_at timestamptz, notes jsonb)
language plpgsql stable security definer set search_path='' as $$
begin
  perform public.require_permission('reports:revenue');
  if p_from is null or p_to is null or p_from > p_to then perform public.fail('bad_input', '22023'); end if;
  return query select s.shift_id, p.full_name, s.opened_at, s.closed_at, s.opening_float,
      coalesce(s.cash_in, t.cash_in), coalesce(s.cash_out, t.cash_out), coalesce(s.expected, s.opening_float + t.cash_in - t.cash_out),
      s.counted, s.counted - s.expected,
      case when s.counted is not null and s.counted < s.expected then s.expected - s.counted else 0 end,
      case when s.counted is not null and s.counted > s.expected then s.counted - s.expected else 0 end,
      s.open_note, s.close_note, s.close_reason, s.resolved_at,
      coalesce((select jsonb_agg(jsonb_build_object('note_id', n.note_id, 'author', coalesce(pr.full_name, pr.username), 'note', n.note, 'resolved', n.resolved, 'created_at', n.created_at)
                  order by n.created_at, n.note_id)
                from public.cash_shift_notes n join public.profiles pr on pr.id = n.author_id
                where n.shift_id = s.shift_id), '[]'::jsonb)
    from public.cash_shifts s join public.profiles p on p.id = s.cashier_id cross join lateral public.cash_shift_totals(s.shift_id) t
    where (s.opened_at at time zone 'Asia/Manila')::date between p_from and p_to
    order by s.opened_at desc;
end $$;

revoke all on function public.close_cash_shift(numeric, text, text), public.add_cash_shift_note(bigint, text, boolean),
  public.cash_shifts_list(date, date) from public, anon, authenticated;
grant execute on function public.close_cash_shift(numeric, text, text), public.add_cash_shift_note(bigint, text, boolean),
  public.cash_shifts_list(date, date) to authenticated;
