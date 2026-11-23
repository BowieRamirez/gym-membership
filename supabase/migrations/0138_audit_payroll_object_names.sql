-- Phase 19 follow-up (2026-11-22): Human-readable Object names for payroll audit rows in Logs & Archive.

do $$
declare v_def text;
begin
  v_def := pg_get_functiondef('public.audit_records(date,date,text,text,text,integer,integer,boolean,text,text,text,text)'::regprocedure);
  if position('staff_salaries' in v_def) = 0 then
    v_def := replace(v_def,
      $q$o.full_name object_name,$q$,
      $q$coalesce(
				o.full_name,
				pl_u.full_name,
				case
					when a.entity = 'role_salaries' then case a.entity_id when 'employee' then 'Front desk' when 'coach' then 'Coach' else a.entity_id end
					when a.entity = 'payroll_periods' and pp.period_id is not null then to_char(pp.starts_on, 'FMDD') || '–' || to_char(pp.ends_on, 'FMDD Mon YYYY')
				end
			) object_name,$q$);
    v_def := replace(v_def,
      $q$left join public.profiles o on a.entity in ('profiles', 'user_roles') and o.id::text = a.entity_id$q$,
      $q$left join public.profiles o on a.entity in ('profiles', 'user_roles', 'staff_salaries') and o.id::text = a.entity_id
		left join public.payroll_periods pp on a.entity = 'payroll_periods' and pp.period_id::text = a.entity_id
		left join public.profiles pl_u on a.entity = 'payroll_lines' and pl_u.id::text = (a.details ->> 'user_id')$q$);
    execute v_def;
  end if;
end $$;
