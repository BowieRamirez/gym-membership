-- 0017: live chat, and "last active".
--
-- 1. messages and support_messages join the Realtime publication, so the app is told when a row is added or changed. The app treats
--    that as a hint only (it asks chat_messages() again), and Realtime sends a change only to people whose row access lets them read
--    the row, so staff still never hear about a coach chat.
-- 2. user_last_seen: when someone was last in the app. A separate table and not a profiles column, because 0006 lets every signed-in
--    user select every column of any profile row they can see (staff see them all). This table has no grant and no policy, so the only
--    reader is chat_threads() below.

alter publication supabase_realtime add table public.messages, public.support_messages;

create table public.user_last_seen (
	user_id uuid        primary key references public.profiles (id) on delete cascade,
	seen_at timestamptz not null
);
alter table public.user_last_seen enable row level security;
revoke all on public.user_last_seen from anon, authenticated;

-- The app calls this on open and every few minutes while open. Called sooner than 5 minutes after the last time, it changes nothing.
create function public.touch_last_seen() returns void
language plpgsql security definer
set search_path = ''
as $$
begin
	perform public.require_active_user();
	insert into public.user_last_seen (user_id, seen_at) values (auth.uid(), now())
	on conflict (user_id) do update set seen_at = now()
	where public.user_last_seen.seen_at < now() - interval '5 minutes';
end;
$$;

revoke all on function public.touch_last_seen() from public, anon, authenticated;
grant execute on function public.touch_last_seen() to authenticated;

-- Same as 0016 plus with_last_seen (the last column):
--   a coach chat: the other person's last seen, only while the hire is current
--   a member's own front desk thread: null (staff come and go, so there is no one to be "active")
--   the front desk inbox: the member's last seen
-- The return type changes, so the function is replaced.
drop function public.chat_threads();
create function public.chat_threads()
returns table (kind text, coach_id uuid, member_id uuid, with_id uuid, with_name text, with_avatar text, last_body text, last_at timestamptz,
               last_from_me boolean, unread integer, can_send boolean, status text, claimed_by uuid, claimed_by_name text, with_last_seen timestamptz)
language plpgsql stable security definer
set search_path = ''
as $$
#variable_conflict use_column
declare
	v_uid uuid := auth.uid();
begin
	perform public.require_active_user();

	return query
	select * from (
		-- coach chats
		select 'coach'::text as kind, p.coach_id as coach_id, p.member_id as member_id,
		       case when p.coach_id = v_uid then p.member_id else p.coach_id end as with_id,
		       pr.full_name as with_name, pr.avatar_path as with_avatar,
		       coalesce(nullif(btrim(lm.body), ''), case when lm.attachment_path is not null then 'Photo' end) as last_body,
		       lm.sent_at as last_at, coalesce(lm.sender_id = v_uid, false) as last_from_me,
		       (select count(*)::integer from public.messages x
		         where x.coach_id = p.coach_id and x.member_id = p.member_id and x.sender_id <> v_uid and x.read_at is null) as unread,
		       (public.coaches_member(p.coach_id, p.member_id) and public.is_active_member(p.member_id)) as can_send,
		       null::text as status, null::uuid as claimed_by, null::text as claimed_by_name,
		       case when public.coaches_member(p.coach_id, p.member_id)
		            then (select ls.seen_at from public.user_last_seen ls where ls.user_id = case when p.coach_id = v_uid then p.member_id else p.coach_id end)
		       end as with_last_seen
		from (
			select h.coach_id, h.member_id from public.coach_hires h where h.coach_id = v_uid or h.member_id = v_uid
			union
			select m.coach_id, m.member_id from public.messages m where m.coach_id = v_uid or m.member_id = v_uid
		) p
		join public.profiles pr on pr.id = case when p.coach_id = v_uid then p.member_id else p.coach_id end
		left join lateral (select x.body, x.attachment_path, x.sent_at, x.sender_id from public.messages x
		                   where x.coach_id = p.coach_id and x.member_id = p.member_id order by x.message_id desc limit 1) lm on true

		union all

		-- a member's own front-desk thread (always there, even with no messages yet)
		select 'support'::text, null::uuid, mb.user_id, null::uuid, 'Front desk'::text, null::text,
		       coalesce(nullif(btrim(lm.body), ''), case when lm.attachment_path is not null then 'Photo' end), lm.sent_at, coalesce(lm.sender_id = v_uid, false),
		       (select count(*)::integer from public.support_messages s where s.member_id = mb.user_id and s.sender_id <> v_uid and s.read_at is null),
		       true, coalesce(t.status, 'open'), null::uuid, null::text, null::timestamptz
		from public.members mb
		left join public.support_threads t on t.member_id = mb.user_id
		left join lateral (select s.body, s.attachment_path, s.sent_at, s.sender_id from public.support_messages s where s.member_id = mb.user_id order by s.message_id desc limit 1) lm on true
		where mb.user_id = v_uid

		union all

		-- the inbox, for staff who may answer
		select 'support'::text, null::uuid, t.member_id, t.member_id, pr.full_name, pr.avatar_path,
		       coalesce(nullif(btrim(lm.body), ''), case when lm.attachment_path is not null then 'Photo' end), lm.sent_at, coalesce(lm.sender_id <> t.member_id, false),
		       (select count(*)::integer from public.support_messages s where s.member_id = t.member_id and s.sender_id = t.member_id and s.read_at is null),
		       true, t.status, t.claimed_by, cb.full_name,
		       (select ls.seen_at from public.user_last_seen ls where ls.user_id = t.member_id)
		-- the 500 most recently active threads, so the list stays small however many members have written
		from (select * from public.support_threads st where public.has_permission('support:reply') and st.member_id <> v_uid
		      order by st.last_message_at desc limit 500) t
		join public.profiles pr on pr.id = t.member_id
		left join public.profiles cb on cb.id = t.claimed_by
		left join lateral (select s.body, s.attachment_path, s.sent_at, s.sender_id from public.support_messages s where s.member_id = t.member_id order by s.message_id desc limit 1) lm on true
	) q
	order by q.last_at desc nulls last;
end;
$$;

revoke all on function public.chat_threads() from public, anon, authenticated;
grant execute on function public.chat_threads() to authenticated;
