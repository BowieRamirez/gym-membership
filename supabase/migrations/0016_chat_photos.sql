-- 0016: photos in chat (role redesign, phase 4b)
--
-- A photo is uploaded to the private chat-attachments bucket, then sent as a message that remembers its path.
-- Where a photo may live, and who may read or add it, is decided in one function used by both storage policies.

insert into storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
values ('chat-attachments', 'chat-attachments', false, 5242880, array['image/png', 'image/jpeg', 'image/webp'])
on conflict (id) do update
set public = excluded.public, file_size_limit = excluded.file_size_limit, allowed_mime_types = excluded.allowed_mime_types;

-- ---------------------------------------------------------------------
-- Columns and rules
-- ---------------------------------------------------------------------

alter table public.messages         add column attachment_path text;
alter table public.support_messages add column attachment_path text;

-- the text may be empty when a photo is attached
alter table public.messages drop constraint messages_body_check;
alter table public.messages add constraint messages_text_or_photo check (length(trim(body)) > 0 or attachment_path is not null);
alter table public.support_messages drop constraint support_messages_body_check;
alter table public.support_messages add constraint support_messages_text_or_photo
	check ((char_length(btrim(body)) > 0 or attachment_path is not null) and char_length(body) <= 2000);

-- a photo belongs to the conversation it is sent in: {coach}/{member}/{file}, or support/{member}/{file}
alter table public.messages add constraint messages_photo_in_conversation
	check (attachment_path is null or attachment_path ~ ('^' || coach_id::text || '/' || member_id::text || '/[A-Za-z0-9_-][A-Za-z0-9._-]{0,119}$'));
alter table public.support_messages add constraint support_messages_photo_in_thread
	check (attachment_path is null or attachment_path ~ ('^support/' || member_id::text || '/[A-Za-z0-9_-][A-Za-z0-9._-]{0,119}$'));

grant insert (attachment_path) on public.messages to authenticated;

-- ---------------------------------------------------------------------
-- Who may use a file
-- ---------------------------------------------------------------------

-- Reading: the two people of a coach chat; the member and staff with support:reply for the front desk.
-- Adding: the same, and for a coach chat also an active hire and an active plan (as for text).
create function public.can_use_chat_file(p_name text, p_write boolean) returns boolean
language plpgsql stable security definer
set search_path = ''
as $$
declare
	v_uid  uuid := auth.uid();
	v_id   constant text := '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}';
	v_file constant text := '[A-Za-z0-9_-][A-Za-z0-9._-]{0,119}$';
	v_coach uuid;
	v_member uuid;
begin
	if v_uid is null or p_name is null or not public.is_active_user() then return false; end if;

	if p_name ~ ('^support/' || v_id || '/' || v_file) then
		v_member := split_part(p_name, '/', 2)::uuid;
		return v_member = v_uid or public.has_permission('support:reply');
	elsif p_name ~ ('^' || v_id || '/' || v_id || '/' || v_file) then
		v_coach := split_part(p_name, '/', 1)::uuid;
		v_member := split_part(p_name, '/', 2)::uuid;
		if v_uid <> v_coach and v_uid <> v_member then return false; end if;
		return not p_write or (public.coaches_member(v_coach, v_member) and public.is_active_member(v_member));
	end if;
	return false;
end;
$$;

revoke all on function public.can_use_chat_file(text, boolean) from public, anon, authenticated;
grant execute on function public.can_use_chat_file(text, boolean) to authenticated;

create policy "chat attachments: add to your conversation" on storage.objects for insert to authenticated
	with check (bucket_id = 'chat-attachments' and public.can_use_chat_file(name, true));
create policy "chat attachments: read your conversation" on storage.objects for select to authenticated
	using (bucket_id = 'chat-attachments' and public.can_use_chat_file(name, false));

-- ---------------------------------------------------------------------
-- Functions that show or send messages
-- ---------------------------------------------------------------------

-- Same as 0015 except that last_body says "Photo" when the last message has no text.
create or replace function public.chat_threads()
returns table (kind text, coach_id uuid, member_id uuid, with_id uuid, with_name text, with_avatar text, last_body text, last_at timestamptz,
               last_from_me boolean, unread integer, can_send boolean, status text, claimed_by uuid, claimed_by_name text)
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
		       null::text as status, null::uuid as claimed_by, null::text as claimed_by_name
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
		       true, coalesce(t.status, 'open'), null::uuid, null::text
		from public.members mb
		left join public.support_threads t on t.member_id = mb.user_id
		left join lateral (select s.body, s.attachment_path, s.sent_at, s.sender_id from public.support_messages s where s.member_id = mb.user_id order by s.message_id desc limit 1) lm on true
		where mb.user_id = v_uid

		union all

		-- the inbox, for staff who may answer
		select 'support'::text, null::uuid, t.member_id, t.member_id, pr.full_name, pr.avatar_path,
		       coalesce(nullif(btrim(lm.body), ''), case when lm.attachment_path is not null then 'Photo' end), lm.sent_at, coalesce(lm.sender_id <> t.member_id, false),
		       (select count(*)::integer from public.support_messages s where s.member_id = t.member_id and s.sender_id = t.member_id and s.read_at is null),
		       true, t.status, t.claimed_by, cb.full_name
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

-- A page of messages now also says which photo each one carries (the return type changes, so the function is replaced).
drop function public.chat_messages(text, uuid, uuid, bigint, integer);
create function public.chat_messages(p_kind text, p_member uuid, p_coach uuid default null, p_before bigint default null, p_limit integer default 50)
returns table (message_id bigint, sender_id uuid, body text, sent_at timestamptz, delivered_at timestamptz, read_at timestamptz, sender_name text, attachment_path text)
language plpgsql stable security definer
set search_path = ''
as $$
#variable_conflict use_column
declare
	v_uid uuid := auth.uid();
	v_limit integer := least(greatest(coalesce(p_limit, 50), 1), 100);
begin
	perform public.require_active_user();

	if p_kind = 'coach' then
		if v_uid is distinct from p_coach and v_uid is distinct from p_member then perform public.fail('forbidden', '42501'); end if;
		return query
			select m.message_id, m.sender_id, m.body, m.sent_at, m.delivered_at, m.read_at, null::text, m.attachment_path
			from public.messages m
			where m.coach_id = p_coach and m.member_id = p_member and (p_before is null or m.message_id < p_before)
			order by m.message_id desc limit v_limit;
	elsif p_kind = 'support' then
		if v_uid is distinct from p_member and not public.has_permission('support:reply') then perform public.fail('forbidden', '42501'); end if;
		return query
			select s.message_id, s.sender_id, s.body, s.sent_at, s.delivered_at, s.read_at,
			       -- a member never learns which staff member answered; staff do
			       case when v_uid = p_member or s.sender_id = p_member then null else (select pr.full_name from public.profiles pr where pr.id = s.sender_id) end,
			       s.attachment_path
			from public.support_messages s
			where s.member_id = p_member and (p_before is null or s.message_id < p_before)
			order by s.message_id desc limit v_limit;
	else
		perform public.fail('bad_input', '22023');
	end if;
end;
$$;

revoke all on function public.chat_messages(text, uuid, uuid, bigint, integer) from public, anon, authenticated;
grant execute on function public.chat_messages(text, uuid, uuid, bigint, integer) to authenticated;

-- A member writes to the front desk (p_member is themselves), or staff answer a member. The photo is optional.
drop function public.send_support_message(uuid, text);
create function public.send_support_message(p_member uuid, p_body text, p_attachment text default null) returns bigint
language plpgsql security definer
set search_path = ''
as $$
declare
	v_uid uuid := auth.uid();
	v_body text := coalesce(btrim(p_body), '');
	v_att text := nullif(btrim(p_attachment), '');
	v_id bigint;
	v_claimed uuid;
	v_name text;
	v_preview text;
begin
	perform public.require_active_user();
	if v_body = '' and v_att is null then perform public.fail('empty_message', '22023'); end if;
	if char_length(v_body) > 2000 then perform public.fail('message_too_long', '22023'); end if;

	if v_uid = p_member then
		if not exists (select 1 from public.members where user_id = v_uid) then perform public.fail('forbidden', '42501'); end if;
	elsif public.has_permission('support:reply') then
		if not exists (select 1 from public.members where user_id = p_member) then perform public.fail('not_found', '22023'); end if;
	else
		perform public.fail('forbidden', '42501');
	end if;

	-- the photo has to be in this member's front desk folder
	if v_att is not null and v_att !~ ('^support/' || p_member::text || '/[A-Za-z0-9_-][A-Za-z0-9._-]{0,119}$') then perform public.fail('forbidden', '42501'); end if;

	insert into public.support_threads (member_id) values (p_member) on conflict (member_id) do nothing;
	insert into public.support_messages (member_id, sender_id, body, attachment_path) values (p_member, v_uid, v_body, v_att) returning message_id into v_id;
	v_preview := coalesce(nullif(left(v_body, 80), ''), 'Photo');

	if v_uid = p_member then
		update public.support_threads set last_message_at = now(), status = 'open' where member_id = p_member returning claimed_by into v_claimed;
		select full_name into v_name from public.profiles where id = v_uid;
		if v_claimed is not null and exists (select 1 from public.profiles where id = v_claimed and is_active) then
			perform public.notify(v_claimed, 'message', 'New message from ' || v_name, v_preview, jsonb_build_object('support_member', p_member::text));
		else
			perform public.notify_staff('message', 'New message from ' || v_name, v_preview, jsonb_build_object('support_member', p_member::text));
		end if;
	else
		update public.support_threads set last_message_at = now() where member_id = p_member;
		perform public.notify(p_member, 'message', 'Message from the front desk', v_preview, jsonb_build_object('support_member', p_member::text));
	end if;

	return v_id;
end;
$$;

revoke all on function public.send_support_message(uuid, text, text) from public, anon, authenticated;
grant execute on function public.send_support_message(uuid, text, text) to authenticated;
