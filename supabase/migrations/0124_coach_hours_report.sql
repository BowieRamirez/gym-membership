-- Phase 14 (E11): Coach hours report for admins, and "Coach add-on" label in revenue_lines.

do $$
declare v_def text;
begin
  v_def := pg_get_functiondef('public.revenue_lines(date,date,integer,integer)'::regprocedure);
  if position($q$when 'coach_addon'$q$ in v_def) = 0 then
    v_def := replace(v_def,
      $q$when 'amenity' then coalesce(a.name, 'Amenity')$q$,
      $q$when 'amenity' then coalesce(a.name, 'Amenity')
        when 'coach_addon' then 'Coach add-on'$q$);
    execute v_def;
  end if;

  v_def := pg_get_functiondef('public.audit_records(date,date,text,text,text,integer,integer,boolean,text,text,text,text)'::regprocedure);
  if position($q$'coach_addon'$q$ in v_def) = 0 then
    v_def := replace(v_def,
      $q$('' , 'membership', 'amenity', 'day_pass', 'refund', 'cash_shift')$q$,
      $q$('', 'membership', 'amenity', 'day_pass', 'coach_addon', 'refund', 'cash_shift')$q$);
    v_def := replace(v_def,
      $q$('' ,'membership','amenity','day_pass','refund','cash_shift')$q$,
      $q$('','membership','amenity','day_pass','coach_addon','refund','cash_shift')$q$);
    v_def := replace(v_def,
      $q$('', 'membership', 'amenity', 'day_pass', 'refund', 'cash_shift')$q$,
      $q$('', 'membership', 'amenity', 'day_pass', 'coach_addon', 'refund', 'cash_shift')$q$);
    v_def := replace(v_def,
      $q$or p_what = 'amenity' and x.area = 'payment' and x.kind = 'amenity')$q$,
      $q$or p_what = 'amenity' and x.area = 'payment' and x.kind = 'amenity'
			or p_what = 'coach_addon' and x.area = 'payment' and x.kind = 'coach_addon')$q$);
    execute v_def;
  end if;
end $$;

create or replace function public.coach_hours_report(p_from date, p_to date)
returns table (
  coach_id uuid,
  coach_name text,
  completed_sessions int,
  completed_hours numeric,
  no_shows int,
  coach_cancelled int
)
language plpgsql stable security definer
set search_path = ''
as $$
begin
  perform public.require_active_user();
  if not (public.has_role('admin') or public.has_role('subadmin')) then
    perform public.fail('forbidden', '42501');
  end if;
  if p_from is null or p_to is null or p_from > p_to then
    perform public.fail('bad_input', '22023');
  end if;

  return query
  select c.user_id as coach_id,
         pr.full_name as coach_name,
         count(*) filter (where ts.status = 'completed')::int as completed_sessions,
         coalesce(sum(extract(epoch from (ts.scheduled_end - ts.scheduled_start)) / 3600.0)
           filter (where ts.status = 'completed'), 0)::numeric(10,2) as completed_hours,
         count(*) filter (where ts.status = 'no_show')::int as no_shows,
         count(*) filter (where ts.status = 'cancelled' and ts.cancelled_by = c.user_id)::int as coach_cancelled
  from public.coaches c
  join public.profiles pr on pr.id = c.user_id
  left join public.training_sessions ts
    on ts.coach_id = c.user_id
   and (ts.scheduled_start at time zone 'Asia/Manila')::date between p_from and p_to
   and (ts.held_until is null or ts.held_until > now())
  group by c.user_id, pr.full_name
  order by pr.full_name;
end $$;

revoke all on function public.coach_hours_report(date, date) from public, anon, authenticated;
grant execute on function public.coach_hours_report(date, date) to authenticated;
