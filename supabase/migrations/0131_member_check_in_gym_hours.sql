-- Owner, 2026-11-21: a member check-in outside opening hours is refused `gym_closed`, the same rule coach check-in has had since 0032.
-- Judged at the check-in's own time, so a queued offline check-in made during opening hours still goes through later.
-- Only record_check_in: walk-in Day pass check-ins made by a sale or a late Xendit confirmation are left alone.
do $$
declare
  v_def text := pg_get_functiondef('public.record_check_in(uuid,text,timestamptz,uuid,text,text,uuid,text)'::regprocedure);
  v_anchor text := $q$if not exists (select 1 from public.members where user_id=p_member_id) then perform public.fail('not_found','22023'); end if;$q$;
begin
  if position(v_anchor in v_def) = 0 then raise exception '0131: record_check_in lacks the expected member check'; end if;
  execute replace(v_def, v_anchor, v_anchor || $q$
  if exists (select 1 from public.gym_settings g where g.id = 1
      and ((v_at at time zone 'Asia/Manila')::time < g.opens_at or (v_at at time zone 'Asia/Manila')::time >= g.closes_at)) then
    perform public.fail('gym_closed','22023');
  end if;$q$);
end $$;

revoke all on function public.record_check_in(uuid,text,timestamptz,uuid,text,text,uuid,text) from public, anon;
grant execute on function public.record_check_in(uuid,text,timestamptz,uuid,text,text,uuid,text) to authenticated;
