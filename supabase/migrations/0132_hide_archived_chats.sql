-- Owner, 2026-11-21: an archived account's chats leave everyone's Messages list (coach chats and the desk inbox).
-- The messages are kept; restoring the account brings the chats back.
do $$
declare
  v_def text := pg_get_functiondef('public.chat_threads()'::regprocedure);
  v_coach text := $q$join public.profiles pr on pr.id = case when p.coach_id = v_uid then p.member_id else p.coach_id end$q$;
  v_inbox text := $q$join public.profiles pr on pr.id = t.member_id$q$;
begin
  if position(v_coach in v_def) = 0 or position(v_inbox in v_def) = 0 then raise exception '0132: chat_threads lacks the expected joins'; end if;
  v_def := replace(v_def, v_coach, v_coach || ' and pr.deleted_at is null');
  v_def := replace(v_def, v_inbox, v_inbox || ' and pr.deleted_at is null');
  execute v_def;
end $$;

revoke all on function public.chat_threads() from public, anon, authenticated;
grant execute on function public.chat_threads() to authenticated;
