-- Item 1 (2026-11-05): a name is master data, locked once the account exists. This reverses "editable in Settings" from 0035.
-- The lock is the table privilege, so it holds for any client, not just this app's screens. New accounts still get their name
-- from handle_new_user. The only way to change one afterwards is correct_name, for a mistake, by a system admin (role admin).

revoke update (full_name, first_name, last_name) on public.profiles from authenticated;
drop function public.set_my_name(text, text);

create function public.correct_name(p_user uuid, p_first text, p_last text, p_reason text) returns void
language plpgsql security definer set search_path='' as $$
declare
  v_old text;
  v_new text;
  v_reason text := btrim(coalesce(p_reason,''));
begin
  perform public.require_archive_admin();   -- users:manage and the admin role, as for other account actions only they may take
  if btrim(coalesce(p_first,'')) = '' or btrim(coalesce(p_last,'')) = '' then perform public.fail('bad_name'); end if;
  if char_length(v_reason) not between 1 and 500 then perform public.fail('bad_correction_reason'); end if;
  select full_name into v_old from public.profiles where id=p_user for update;
  if not found then perform public.fail('not_found'); end if;
  -- the table's letter rules check the parts, the trigger from 0035 rebuilds full_name, and an archived account is refused by 0024
  update public.profiles set first_name=btrim(p_first), last_name=btrim(p_last) where id=p_user returning full_name into v_new;
  perform public.audit('user.correct_name','profiles',p_user::text,jsonb_build_object('from',v_old,'to',v_new,'reason',v_reason));
end $$;
revoke all on function public.correct_name(uuid,text,text,text) from public, anon;
grant execute on function public.correct_name(uuid,text,text,text) to authenticated;
