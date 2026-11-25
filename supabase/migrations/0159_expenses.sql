-- Owner fixes 2026-11-25, Fix 5 (X1-X4): the money the gym spends to operate, and the payroll cost the financial report reads.
-- Expenses are added and voided (never edited or deleted) through these functions only; the table is closed to the app.
-- Payroll is never entered as an expense: payroll_cost reads the gross pay of approved or paid-out pay periods.

create table public.expenses (
  expense_id bigint generated always as identity primary key,
  spent_on date not null,
  category text not null check (category in ('electricity', 'water', 'rent', 'internet', 'equipment', 'repairs', 'cleaning', 'salaries', 'other')),
  amount numeric(12,2) not null check (amount > 0 and amount <= 10000000),
  note text not null check (length(btrim(note)) between 1 and 200),
  created_by uuid not null references public.profiles(id),
  created_at timestamptz not null default now(),
  voided_at timestamptz,
  voided_by uuid references public.profiles(id),
  void_reason text check (void_reason is null or length(btrim(void_reason)) between 1 and 200),
  check ((voided_at is null) = (voided_by is null) and (voided_at is null) = (void_reason is null))
);
create index expenses_spent_on on public.expenses (spent_on);
alter table public.expenses enable row level security;
revoke all on table public.expenses from public, anon, authenticated;

create function public.expense_add(p_spent_on date, p_category text, p_amount numeric, p_note text)
returns public.expenses
language plpgsql security definer set search_path = '' as $$
declare v public.expenses;
begin
  perform public.require_permission('reports:revenue');
  if p_spent_on is null or p_spent_on > (now() at time zone 'Asia/Manila')::date
     or p_category is null or p_category not in ('electricity', 'water', 'rent', 'internet', 'equipment', 'repairs', 'cleaning', 'salaries', 'other')
     or p_amount is null or p_amount <= 0 or p_amount > 10000000 or round(p_amount, 2) <> p_amount
     or p_note is null or length(btrim(p_note)) not between 1 and 200 then
    perform public.fail('bad_input', '22023');
  end if;
  insert into public.expenses (spent_on, category, amount, note, created_by)
  values (p_spent_on, p_category, p_amount, btrim(p_note), auth.uid()) returning * into v;
  perform public.audit('expense.added', 'expenses', v.expense_id::text,
    jsonb_build_object('spent_on', v.spent_on, 'category', v.category, 'amount', v.amount, 'note', v.note));
  return v;
end $$;

create function public.expense_void(p_expense bigint, p_reason text)
returns void
language plpgsql security definer set search_path = '' as $$
declare v public.expenses;
begin
  perform public.require_permission('reports:revenue');
  if p_expense is null or p_reason is null or length(btrim(p_reason)) not between 1 and 200 then perform public.fail('bad_input', '22023'); end if;
  select * into v from public.expenses where expense_id = p_expense for update;
  if not found then perform public.fail('not_found', '22023'); end if;
  if v.voided_at is not null then perform public.fail('already_processed', '22023'); end if;
  update public.expenses set voided_at = now(), voided_by = auth.uid(), void_reason = btrim(p_reason) where expense_id = p_expense;
  perform public.audit('expense.voided', 'expenses', p_expense::text,
    jsonb_build_object('category', v.category, 'amount', v.amount, 'reason', btrim(p_reason)));
end $$;

create function public.expenses_list(p_from date, p_to date)
returns table (expense_id bigint, spent_on date, category text, amount numeric, note text, created_by_name text, created_at timestamptz,
               voided_at timestamptz, voided_by_name text, void_reason text)
language plpgsql stable security definer set search_path = '' as $$
begin
  perform public.require_permission('reports:revenue');
  if p_from is null or p_to is null or p_to < p_from then perform public.fail('bad_input', '22023'); end if;
  return query
  select e.expense_id, e.spent_on, e.category, e.amount, e.note, coalesce(nullif(btrim(c.full_name), ''), c.username), e.created_at,
         e.voided_at, coalesce(nullif(btrim(v.full_name), ''), v.username), e.void_reason
  from public.expenses e
  join public.profiles c on c.id = e.created_by
  left join public.profiles v on v.id = e.voided_by
  where e.spent_on between p_from and p_to
  order by e.spent_on desc, e.expense_id desc;
end $$;

-- X4: gross pay (base pay + bonuses, from the lines frozen when the period was approved) of each approved or paid-out period, on its last day
create function public.payroll_cost(p_from date, p_to date)
returns table (day date, gross numeric)
language plpgsql stable security definer set search_path = '' as $$
begin
  perform public.require_permission('reports:revenue');
  if p_from is null or p_to is null or p_to < p_from then perform public.fail('bad_input', '22023'); end if;
  return query
  select pp.ends_on, coalesce(sum(pl.amount) filter (where pl.kind in ('salary', 'bonus')), 0)::numeric(12,2)
  from public.payroll_periods pp
  left join public.payroll_lines pl on pl.period_id = pp.period_id
  where pp.paid_at is not null and pp.ends_on between p_from and p_to
  group by pp.period_id, pp.ends_on
  order by pp.ends_on;
end $$;

revoke all on function public.expense_add(date, text, numeric, text) from public, anon;
revoke all on function public.expense_void(bigint, text) from public, anon;
revoke all on function public.expenses_list(date, date) from public, anon;
revoke all on function public.payroll_cost(date, date) from public, anon;
grant execute on function public.expense_add(date, text, numeric, text) to authenticated;
grant execute on function public.expense_void(bigint, text) to authenticated;
grant execute on function public.expenses_list(date, date) to authenticated;
grant execute on function public.payroll_cost(date, date) to authenticated;
