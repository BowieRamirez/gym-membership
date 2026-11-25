-- Owner fixes 2026-11-25, Fix 4 Phase 4.1 (W2, W6): Attendance Review lists every person's time records and hour corrections for a
-- period in one call, each record with whose it is and how they are paid (the money table, W4). Read only; approving stays
-- payroll_review_time. Same records and permission as payroll_records_for (0152) and payroll_adjustments_for (0142).

create function public.payroll_records_all(p_period bigint)
returns jsonb
language plpgsql stable security definer set search_path = '' as $$
begin
  perform public.require_permission('reports:revenue');
  if p_period is null then perform public.fail('bad_input', '22023'); end if;
  return jsonb_build_object(
    'records', coalesce((
      select jsonb_agg(jsonb_build_object(
               'user_id', r.user_id, 'full_name', p.full_name, 'role_name', p.role, 'pay_type', p.pay_type, 'rate', p.rate,
               'source', r.source, 'source_id', r.source_id, 'starts_at', r.starts_at, 'ends_at', r.ends_at, 'hours', r.hours,
               'label', r.label, 'status', r.status, 'reason', r.reason, 'flags', to_jsonb(r.flags),
               'actual_start', r.actual_start, 'expected_start', r.expected_start, 'expected_end', r.expected_end)
             order by p.full_name, r.starts_at, r.source_id)
      from public.payroll_records(p_period) r join public.payroll_pay(p_period) p on p.user_id = r.user_id), '[]'::jsonb),
    'adjustments', coalesce((
      select jsonb_agg(jsonb_build_object(
               'adjustment_id', a.adjustment_id, 'user_id', a.user_id, 'full_name', coalesce(nullif(btrim(w.full_name), ''), w.username),
               'quantity', a.quantity, 'note', a.note, 'created_by_name', coalesce(nullif(btrim(b.full_name), ''), b.username),
               'created_at', a.created_at)
             order by a.created_at, a.adjustment_id)
      from public.payroll_hour_adjustments a
      join public.profiles w on w.id = a.user_id
      join public.profiles b on b.id = a.created_by
      where a.period_id = p_period), '[]'::jsonb));
end $$;

revoke all on function public.payroll_records_all(bigint) from public, anon;
grant execute on function public.payroll_records_all(bigint) to authenticated;
