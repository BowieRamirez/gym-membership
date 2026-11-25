-- Owner fixes 2026-11-25, Fix 3 Phase 3.4: the desk's coach list shows each coach's shift today and who is late. The schedule tables
-- are readable only by their owner and payroll staff (0151), and coach shift_posts start tomorrow, so the desk reads today's shifts here.
-- Late (server time): checked in after the start, or not in while the shift has started and not ended.
create function public.coach_shifts_today() returns table (coach_id uuid, starts_at timestamptz, ends_at timestamptz, late boolean)
language plpgsql stable security definer set search_path = '' as $$
begin
  perform public.require_permission('check_ins:record');
  return query
  select c.user_id, f.starts_at, f.ends_at,
         coalesce(v.checked_in_at > f.starts_at, now() > f.starts_at and now() < f.ends_at)
  from public.coaches c join public.profiles p on p.id = c.user_id and p.is_active
  cross join lateral public.shift_for(c.user_id, (now() at time zone 'Asia/Manila')::date) f
  left join public.coach_visits v on v.coach_id = c.user_id and v.checked_out_at is null;
end $$;
revoke all on function public.coach_shifts_today() from public, anon;
grant execute on function public.coach_shifts_today() to authenticated;
