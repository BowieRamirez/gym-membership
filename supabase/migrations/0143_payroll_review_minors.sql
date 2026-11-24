-- 0143: small fixes after the 0142 review (plan 2026-11-24-hourly-payroll.md, deferred minors).
-- 1. payroll_review_time: a missing status is bad_input (was a raw not-null error).
-- 2. use_role_salary: someone already following the role default changes nothing and writes no log row.
-- 3. payroll_basis: a paid period's "Approved" hours come from the frozen line (payable - corrections), not from records that changed later.

create or replace function public.payroll_basis(p_period bigint)
returns table (user_id uuid, full_name text, role text, pay_type text, rate numeric, no_salary boolean, personal boolean,
               logged numeric, adjustment numeric, payable numeric, base_pay numeric, bonuses numeric, deductions numeric,
               gross numeric, net numeric, pending int, flagged int)
language sql stable set search_path = '' as $$
  with per as (select * from public.payroll_periods where period_id = p_period),
  recs as (select * from public.payroll_records(p_period)),
  calc as (
    select pay.*,
           case pay.pay_type
             when 'hourly' then coalesce((
               select round(sum(extract(epoch from (upper(x) - lower(x)))) / 3600.0, 2)
               from unnest((select range_agg(tstzrange(rc.starts_at, rc.ends_at)) from recs rc
                            where rc.user_id = pay.user_id and rc.status = 'approved' and rc.ends_at is not null)) x), 0)
             when 'per_session' then (select count(*) from recs rc where rc.user_id = pay.user_id and rc.status = 'approved')
             else 0 end::numeric(8,2) as logged,
           coalesce((select sum(a.quantity) from public.payroll_hour_adjustments a where a.period_id = p_period and a.user_id = pay.user_id), 0)::numeric(8,2) as adjustment,
           -- a paid period can't be decided any more, so nothing in it is pending (a shift closed after paying stays out)
           case when (select paid_at from per) is not null then 0
                else (select count(*) from recs rc where rc.user_id = pay.user_id and rc.status = 'pending' and rc.ends_at is not null) end::int as pending,
           (select count(*) from recs rc where rc.user_id = pay.user_id and cardinality(rc.flags) > 0)::int as flagged,
           coalesce((select sum(pl.amount) from public.payroll_lines pl where pl.period_id = p_period and pl.user_id = pay.user_id and pl.kind = 'bonus'), 0)::numeric(12,2) as bonuses,
           coalesce((select sum(pl.amount) from public.payroll_lines pl where pl.period_id = p_period and pl.user_id = pay.user_id and pl.kind = 'deduction'), 0)::numeric(12,2) as deductions,
           (select pl from public.payroll_lines pl where pl.period_id = p_period and pl.user_id = pay.user_id and pl.kind = 'salary' order by pl.line_id limit 1) as frozen
    from public.payroll_pay(p_period) pay
  ),
  based as (
    select c.*,
           case when (select paid_at from per) is not null then coalesce((c.frozen).quantity, case when c.pay_type = 'fixed' then 1 else 0 end)
                when c.pay_type = 'fixed' then 1
                else greatest(0, c.logged + c.adjustment) end::numeric(8,2) as payable,
           case when (select paid_at from per) is not null then coalesce((c.frozen).amount, 0)
                when c.pay_type = 'fixed' then c.rate
                else round(greatest(0, c.logged + c.adjustment) * c.rate, 2) end::numeric(12,2) as base_pay
    from calc c
  )
  -- 3: once paid, "Approved" is what the frozen line was paid for (payable minus corrections), not today's records
  select b.user_id, b.full_name, b.role, b.pay_type, b.rate, b.no_salary, b.personal,
         case when (select paid_at from per) is not null
              then case when b.pay_type = 'fixed' then 0 else greatest(0, b.payable - b.adjustment) end
              else b.logged end::numeric(8,2),
         b.adjustment, b.payable, b.base_pay,
         b.bonuses, b.deductions, (b.base_pay + b.bonuses)::numeric(12,2), (b.base_pay + b.bonuses - b.deductions)::numeric(12,2),
         b.pending, b.flagged
  from based b
$$;

create or replace function public.use_role_salary(p_user uuid) returns void
language plpgsql security definer set search_path = '' as $$
declare
  v_old public.staff_salaries;
  v_role text := public.payroll_role(p_user);
  v_def public.role_salaries;
begin
  perform public.require_permission('reports:revenue');
  if p_user is null or v_role is null or v_role not in ('employee', 'coach') then perform public.fail('bad_input', '22023'); end if;
  if p_user = auth.uid() then perform public.fail('own_salary', '42501'); end if;
  delete from public.staff_salaries where user_id = p_user returning * into v_old;
  if v_old.user_id is null then return; end if;   -- 2: already following the role: nothing changed, nothing to log
  select * into v_def from public.role_salaries where role = v_role;
  perform public.audit('payroll.salary_set', 'staff_salaries', p_user::text,
    jsonb_build_object('old', v_old.amount, 'new', v_def.amount, 'old_type', v_old.pay_type, 'new_type', v_def.pay_type, 'follows_role', true));
end $$;

create or replace function public.payroll_review_time(p_period bigint, p_source text, p_source_id bigint, p_status text, p_reason text default null)
returns void
language plpgsql security definer set search_path = '' as $$
declare
  v_rec record;
  v_reason text := nullif(btrim(coalesce(p_reason, '')), '');
begin
  perform public.require_permission('reports:revenue');
  if p_period is null or p_source is null or p_source_id is null or p_status is null or p_status not in ('approved', 'rejected', 'pending')
     or (p_status = 'rejected' and (v_reason is null or length(v_reason) > 200)) then
    perform public.fail('bad_input', '22023');
  end if;
  perform public.payroll_draft(p_period);
  select * into v_rec from public.payroll_records(p_period) r where r.source = p_source and r.source_id = p_source_id;
  if not found then perform public.fail('not_found', 'P0002'); end if;
  if v_rec.ends_at is null then perform public.fail('record_open', '22023'); end if;
  if p_status = 'pending' then
    delete from public.payroll_time_reviews where source = p_source and source_id = p_source_id;
  else
    insert into public.payroll_time_reviews(source, source_id, status, reason, reviewed_by, reviewed_at)
    values (p_source, p_source_id, p_status, case when p_status = 'rejected' then v_reason end, auth.uid(), now())
    on conflict (source, source_id) do update set status = excluded.status, reason = excluded.reason,
                                                  reviewed_by = excluded.reviewed_by, reviewed_at = excluded.reviewed_at;
  end if;
  perform public.audit(case p_status when 'approved' then 'payroll.time_approved' when 'rejected' then 'payroll.time_rejected'
                                     else 'payroll.time_reopened' end,
    'payroll_time_reviews', p_source || ':' || p_source_id,
    jsonb_build_object('period_id', p_period, 'user_id', v_rec.user_id, 'label', v_rec.label, 'old', v_rec.status,
                       'new', p_status, 'reason', v_reason));
end $$;

-- recreated functions: explicit grants again (live default privileges reopen recreated functions)
revoke all on function public.payroll_basis(bigint) from public, anon, authenticated;
revoke all on function public.use_role_salary(uuid), public.payroll_review_time(bigint, text, bigint, text, text) from public, anon, authenticated;
grant execute on function public.use_role_salary(uuid), public.payroll_review_time(bigint, text, bigint, text, text) to authenticated;
