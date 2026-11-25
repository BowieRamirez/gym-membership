-- Owner fixes 2026-11-25, Fix 3 Phase 3.2 (S6, S7): a check-in or drawer shift with a scheduled shift (0151) pays from the later of
-- the real start and the shift start; the real start is kept (actual_start). Late (in after the shift start) and Left early (out
-- before the shift end) are flags, so Approve all leaves them for the admin. Records with no shift pay as before.
-- starts_at stays the column every payroll sum reads (range_agg, overlap, Approve all), now holding the payable start.

drop function public.payroll_records_for(bigint, uuid);
drop function public.payroll_records(bigint);

create function public.payroll_records(p_period bigint)
returns table (user_id uuid, source text, source_id bigint, starts_at timestamptz, ends_at timestamptz, hours numeric,
               label text, status text, reason text, flags text[],
               actual_start timestamptz, expected_start timestamptz, expected_end timestamptz)
language sql stable set search_path = '' as $$
  with per as (select * from public.payroll_periods where period_id = p_period),
  pay as (select * from public.payroll_pay(p_period)),
  raw0 as (
    select cs.cashier_id as user_id, 'cash_shift'::text as source, cs.shift_id as source_id, cs.opened_at as actual_start,
           cs.closed_at as ends_at, 'Cash shift #' || cs.shift_id as label, false as auto_closed, cs.expected_start, cs.expected_end
    from public.cash_shifts cs join pay on pay.user_id = cs.cashier_id and pay.pay_type = 'hourly' and pay.role <> 'coach', per
    where (cs.opened_at at time zone 'Asia/Manila')::date between per.starts_on and per.ends_on
    union all
    select cv.coach_id, 'coach_visit', cv.coach_visit_id, cv.checked_in_at, cv.checked_out_at, 'Check-in', cv.auto_closed,
           cv.expected_start, cv.expected_end
    from public.coach_visits cv join pay on pay.user_id = cv.coach_id and pay.pay_type = 'hourly' and pay.role = 'coach', per
    where (cv.checked_in_at at time zone 'Asia/Manila')::date between per.starts_on and per.ends_on
    union all
    select ts.coach_id, 'training_session', ts.training_session_id, ts.scheduled_start, ts.scheduled_end,
           coalesce(nullif(btrim(m.full_name), ''), m.username, nullif(btrim(ts.walk_in_name), ''), 'Walk-in'), false, null, null
    from public.training_sessions ts join pay on pay.user_id = ts.coach_id and pay.pay_type = 'per_session'
    left join public.profiles m on m.id = ts.member_id
    cross join per
    where ts.status = 'completed' and (ts.held_until is null or ts.held_until > now())
      and (ts.scheduled_start at time zone 'Asia/Manila')::date between per.starts_on and per.ends_on
  ),
  -- S6: paid from the shift start when in early; never past the check-out, so in and out before the shift counts nothing
  raw as (
    select r.*, case when r.expected_start is null or r.actual_start >= r.expected_start then r.actual_start
                     when r.ends_at is null then r.expected_start
                     else least(r.expected_start, r.ends_at) end as starts_at
    from raw0 r
  )
  select r.user_id, r.source, r.source_id, r.starts_at, r.ends_at,
         case when r.ends_at is null then 0 else round(extract(epoch from (r.ends_at - r.starts_at)) / 3600.0, 2) end,
         r.label,
         coalesce(tr.status, 'pending'), tr.reason,
         array_remove(array[
           case when r.ends_at is null then 'open' end,
           case when r.auto_closed then 'auto_closed' end,
           case when r.ends_at - r.starts_at > interval '12 hours' then 'long' end,
           case when r.source <> 'training_session' and r.ends_at is not null and exists (
             select 1 from raw o where o.user_id = r.user_id and o.source = r.source and o.source_id <> r.source_id
               and o.ends_at is not null and tstzrange(o.starts_at, o.ends_at) && tstzrange(r.starts_at, r.ends_at)) then 'overlap' end,
           case when r.actual_start > r.expected_start then 'late' end,
           case when r.ends_at < r.expected_end then 'left_early' end
         ], null),
         r.actual_start, r.expected_start, r.expected_end
  from raw r
  left join public.payroll_time_reviews tr on tr.source = r.source and tr.source_id = r.source_id
$$;

create function public.payroll_records_for(p_period bigint, p_user uuid)
returns table (source text, source_id bigint, starts_at timestamptz, ends_at timestamptz, hours numeric, label text,
               status text, reason text, flags text[], actual_start timestamptz, expected_start timestamptz, expected_end timestamptz)
language plpgsql stable security definer set search_path = '' as $$
begin
  perform public.require_permission('reports:revenue');
  if p_period is null or p_user is null then perform public.fail('bad_input', '22023'); end if;
  return query
  select r.source, r.source_id, r.starts_at, r.ends_at, r.hours, r.label, r.status, r.reason, r.flags,
         r.actual_start, r.expected_start, r.expected_end
  from public.payroll_records(p_period) r where r.user_id = p_user
  order by r.starts_at, r.source_id;
end $$;

revoke all on function public.payroll_records(bigint) from public, anon, authenticated;
revoke all on function public.payroll_records_for(bigint, uuid) from public, anon;
grant execute on function public.payroll_records_for(bigint, uuid) to authenticated;
