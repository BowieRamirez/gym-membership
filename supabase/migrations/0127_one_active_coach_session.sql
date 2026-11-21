-- Members may hold at most one active (scheduled) coach session at a time until it is cancelled or finished.

alter table public.gym_settings alter column max_upcoming_coach_sessions set default 1;
update public.gym_settings set max_upcoming_coach_sessions = 1 where id = 1;

do $$
declare v_def text;
begin
  v_def := pg_get_functiondef('public._book_coach_session_core(uuid,uuid,timestamptz,int,text,text,boolean,uuid)'::regprocedure);
  if position('and ts.status = ''scheduled'' and ts.scheduled_start > now()' in v_def) = 0 then
    raise exception '0127: _book_coach_session_core lacks expected upcoming count query';
  end if;
  v_def := replace(
    v_def,
    'and ts.status = ''scheduled'' and ts.scheduled_start > now()',
    'and ts.status = ''scheduled'' and (ts.held_until is null or ts.held_until > now())'
  );
  execute v_def;
end $$;

revoke all on function public._book_coach_session_core(uuid, uuid, timestamptz, int, text, text, boolean, uuid) from public, anon, authenticated;
