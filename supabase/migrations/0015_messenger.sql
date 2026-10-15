-- 0015: messenger (role redesign, phase 4a)
--
-- Delivery tracking on coach chats, a front-desk conversation per member, and functions that do the reading on the server:
-- a summary of your conversations, one page of messages, mark delivered and read, and search. The app never downloads a whole
-- table of messages any more.

alter table public.messages add column delivered_at timestamptz;

-- ---------------------------------------------------------------------
-- Front desk conversations: one thread per member
-- ---------------------------------------------------------------------

create table public.support_threads (
	member_id       uuid        primary key references public.members (user_id) on delete cascade,
	claimed_by      uuid        references public.profiles (id),
	status          text        not null default 'open' check (status in ('open', 'closed')),
	last_message_at timestamptz not null default now(),
	created_at      timestamptz not null default now()
);

create table public.support_messages (
	message_id   bigint      generated always as identity primary key,
	member_id    uuid        not null references public.members (user_id) on delete cascade,
	sender_id    uuid        not null references public.profiles (id),
	body         text        not null check (char_length(btrim(body)) > 0 and char_length(body) <= 2000),
	sent_at      timestamptz not null default now(),
	delivered_at timestamptz,
	read_at      timestamptz
);

create index support_messages_thread_idx on public.support_messages (member_id, message_id desc);

alter table public.support_threads  enable row level security;
alter table public.support_messages enable row level security;
revoke all on public.support_threads, public.support_messages from anon, authenticated;
grant select on public.support_threads, public.support_messages to authenticated;

create policy support_threads_select on public.support_threads for select to authenticated
	using (public.is_active_user() and (member_id = auth.uid() or public.has_permission('support:reply')));
create policy support_messages_select on public.support_messages for select to authenticated
	using (public.is_active_user() and (member_id = auth.uid() or public.has_permission('support:reply')));

insert into public.permissions (resource, action, description) values
	('support', 'reply', 'Answer members'' messages to the front desk');

insert into public.role_permissions (role_id, permission_id)
select r.role_id, p.permission_id from public.roles r join public.permissions p on p.resource = 'support' and p.action = 'reply'
where r.name in ('admin', 'employee');

-- ---------------------------------------------------------------------
-- Reading
-- ---------------------------------------------------------------------

-- Your conversations, newest first: coach chats you are part of (for every hire, current or ended, and for anyone already written to),
-- your own front-desk thread if you are a member, and the whole inbox if you may answer members.
create function public.chat_threads()
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
		       lm.body as last_body, lm.sent_at as last_at, coalesce(lm.sender_id = v_uid, false) as last_from_me,
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
		left join lateral (select x.body, x.sent_at, x.sender_id from public.messages x
		                   where x.coach_id = p.coach_id and x.member_id = p.member_id order by x.message_id desc limit 1) lm on true

		union all

		-- a member's own front-desk thread (always there, even with no messages yet)
		select 'support'::text, null::uuid, mb.user_id, null::uuid, 'Front desk'::text, null::text,
		       lm.body, lm.sent_at, coalesce(lm.sender_id = v_uid, false),
		       (select count(*)::integer from public.support_messages s where s.member_id = mb.user_id and s.sender_id <> v_uid and s.read_at is null),
		       true, coalesce(t.status, 'open'), null::uuid, null::text
		from public.members mb
		left join public.support_threads t on t.member_id = mb.user_id
		left join lateral (select s.body, s.sent_at, s.sender_id from public.support_messages s where s.member_id = mb.user_id order by s.message_id desc limit 1) lm on true
		where mb.user_id = v_uid

		union all

		-- the inbox, for staff who may answer
		select 'support'::text, null::uuid, t.member_id, t.member_id, pr.full_name, pr.avatar_path,
		       lm.body, lm.sent_at, coalesce(lm.sender_id <> t.member_id, false),
		       (select count(*)::integer from public.support_messages s where s.member_id = t.member_id and s.sender_id = t.member_id and s.read_at is null),
		       true, t.status, t.claimed_by, cb.full_name
		from public.support_threads t
		join public.profiles pr on pr.id = t.member_id
		left join public.profiles cb on cb.id = t.claimed_by
		left join lateral (select s.body, s.sent_at, s.sender_id from public.support_messages s where s.member_id = t.member_id order by s.message_id desc limit 1) lm on true
		where public.has_permission('support:reply') and t.member_id <> v_uid
	) q
	order by q.last_at desc nulls last;
end;
$$;

-- One page of a conversation, newest first. p_before is the id of the oldest message already shown.
create function public.chat_messages(p_kind text, p_coach uuid, p_member uuid, p_before bigint default null, p_limit integer default 50)
returns table (message_id bigint, sender_id uuid, body text, sent_at timestamptz, delivered_at timestamptz, read_at timestamptz, sender_name text)
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
			select m.message_id, m.sender_id, m.body, m.sent_at, m.delivered_at, m.read_at, null::text
			from public.messages m
			where m.coach_id = p_coach and m.member_id = p_member and (p_before is null or m.message_id < p_before)
			order by m.message_id desc limit v_limit;
	elsif p_kind = 'support' then
		if v_uid is distinct from p_member and not public.has_permission('support:reply') then perform public.fail('forbidden', '42501'); end if;
		return query
			select s.message_id, s.sender_id, s.body, s.sent_at, s.delivered_at, s.read_at,
			       -- a member never learns which staff member answered; staff do
			       case when v_uid = p_member or s.sender_id = p_member then null else (select pr.full_name from public.profiles pr where pr.id = s.sender_id) end
			from public.support_messages s
			where s.member_id = p_member and (p_before is null or s.message_id < p_before)
			order by s.message_id desc limit v_limit;
	else
		perform public.fail('bad_input', '22023');
	end if;
end;
$$;

-- ---------------------------------------------------------------------
-- Status: delivered and read
-- ---------------------------------------------------------------------

-- You opened a conversation: what you received there is delivered and read, and its alert in the bell is cleared.
create function public.mark_chat_read(p_kind text, p_coach uuid, p_member uuid) returns void
language plpgsql security definer
set search_path = ''
as $$
declare
	v_uid uuid := auth.uid();
begin
	perform public.require_active_user();

	if p_kind = 'coach' then
		if v_uid is distinct from p_coach and v_uid is distinct from p_member then perform public.fail('forbidden', '42501'); end if;
		update public.messages
		set delivered_at = coalesce(delivered_at, now()), read_at = coalesce(read_at, now())
		where coach_id = p_coach and member_id = p_member and sender_id <> v_uid and read_at is null;
		update public.user_notifications set read_at = now()
		where user_id = v_uid and type = 'message' and read_at is null
		  and reference @> jsonb_build_object('coach_id', p_coach::text, 'member_id', p_member::text);
	elsif p_kind = 'support' then
		if v_uid is distinct from p_member and not public.has_permission('support:reply') then perform public.fail('forbidden', '42501'); end if;
		update public.support_messages
		set delivered_at = coalesce(delivered_at, now()), read_at = coalesce(read_at, now())
		where member_id = p_member and read_at is null
		  and ((v_uid = p_member and sender_id <> p_member) or (v_uid <> p_member and sender_id = p_member));
		update public.user_notifications set read_at = now()
		where user_id = v_uid and type = 'message' and read_at is null
		  and reference @> jsonb_build_object('support_member', p_member::text);
	else
		perform public.fail('bad_input', '22023');
	end if;
end;
$$;

-- Your conversation list was loaded: everything addressed to you is at least delivered.
create function public.mark_chats_delivered() returns void
language plpgsql security definer
set search_path = ''
as $$
declare
	v_uid uuid := auth.uid();
begin
	perform public.require_active_user();

	update public.messages set delivered_at = now()
	where (coach_id = v_uid or member_id = v_uid) and sender_id <> v_uid and delivered_at is null;

	update public.support_messages set delivered_at = now()
	where delivered_at is null and sender_id <> v_uid
	  and (member_id = v_uid or (public.has_permission('support:reply') and sender_id = member_id));
end;
$$;

-- ---------------------------------------------------------------------
-- Front desk conversation
-- ---------------------------------------------------------------------

-- A member writes to the front desk (p_member is themselves), or staff answer a member.
create function public.send_support_message(p_member uuid, p_body text) returns bigint
language plpgsql security definer
set search_path = ''
as $$
declare
	v_uid uuid := auth.uid();
	v_body text := btrim(p_body);
	v_id bigint;
	v_claimed uuid;
	v_name text;
begin
	perform public.require_active_user();
	if v_body is null or v_body = '' then perform public.fail('empty_message', '22023'); end if;
	if char_length(v_body) > 2000 then perform public.fail('message_too_long', '22023'); end if;

	if v_uid = p_member then
		if not exists (select 1 from public.members where user_id = v_uid) then perform public.fail('forbidden', '42501'); end if;
	elsif public.has_permission('support:reply') then
		if not exists (select 1 from public.members where user_id = p_member) then perform public.fail('not_found', '22023'); end if;
	else
		perform public.fail('forbidden', '42501');
	end if;

	insert into public.support_threads (member_id) values (p_member) on conflict (member_id) do nothing;
	insert into public.support_messages (member_id, sender_id, body) values (p_member, v_uid, v_body) returning message_id into v_id;

	if v_uid = p_member then
		update public.support_threads set last_message_at = now(), status = 'open' where member_id = p_member returning claimed_by into v_claimed;
		select full_name into v_name from public.profiles where id = v_uid;
		if v_claimed is not null and exists (select 1 from public.profiles where id = v_claimed and is_active) then
			perform public.notify(v_claimed, 'message', 'New message from ' || v_name, left(v_body, 80), jsonb_build_object('support_member', p_member::text));
		else
			perform public.notify_staff('message', 'New message from ' || v_name, left(v_body, 80), jsonb_build_object('support_member', p_member::text));
		end if;
	else
		update public.support_threads set last_message_at = now() where member_id = p_member;
		perform public.notify(p_member, 'message', 'Message from the front desk', left(v_body, 80), jsonb_build_object('support_member', p_member::text));
	end if;

	return v_id;
end;
$$;

-- Take or release a conversation. Taking is for staff when nobody has it; releasing is for whoever took it, or an admin.
create function public.claim_support_thread(p_member uuid, p_claim boolean) returns void
language plpgsql security definer
set search_path = ''
as $$
declare
	v public.support_threads;
begin
	perform public.require_permission('support:reply');
	select * into v from public.support_threads where member_id = p_member for update;
	if not found then perform public.fail('not_found', '22023'); end if;

	if p_claim then
		if v.claimed_by is not null and v.claimed_by <> auth.uid() then perform public.fail('already_processed', 'P0002'); end if;
		update public.support_threads set claimed_by = auth.uid() where member_id = p_member;
	else
		if v.claimed_by is not null and v.claimed_by <> auth.uid() and not public.has_role('admin') then perform public.fail('forbidden', '42501'); end if;
		update public.support_threads set claimed_by = null where member_id = p_member;
	end if;
end;
$$;

create function public.set_support_status(p_member uuid, p_open boolean) returns void
language plpgsql security definer
set search_path = ''
as $$
begin
	perform public.require_permission('support:reply');
	update public.support_threads set status = case when p_open then 'open' else 'closed' end where member_id = p_member;
	if not found then perform public.fail('not_found', '22023'); end if;
end;
$$;

-- ---------------------------------------------------------------------
-- Search: the other person's name, or any message text, among your own conversations only
-- ---------------------------------------------------------------------

create function public.chat_search(p_query text)
returns table (kind text, coach_id uuid, member_id uuid, with_id uuid, with_name text, snippet text, sent_at timestamptz)
language plpgsql stable security definer
set search_path = ''
as $$
#variable_conflict use_column
declare
	v_q text := btrim(coalesce(p_query, ''));
	v_like text;
begin
	perform public.require_active_user();
	if char_length(v_q) < 2 then return; end if;
	-- % and _ typed by the user are plain characters
	v_like := '%' || replace(replace(replace(v_q, '\', '\\'), '%', '\%'), '_', '\_') || '%';

	return query
	select * from (
		select distinct on (t.kind, t.coach_id, t.member_id)
		       t.kind, t.coach_id, t.member_id, t.with_id, t.with_name,
		       coalesce(hit.body, t.last_body) as snippet, coalesce(hit.sent_at, t.last_at) as sent_at
		from public.chat_threads() t
		left join lateral (
			select x.body, x.sent_at from (
				select m.body, m.sent_at from public.messages m
				where t.kind = 'coach' and m.coach_id = t.coach_id and m.member_id = t.member_id and m.body ilike v_like escape '\'
				union all
				select s.body, s.sent_at from public.support_messages s
				where t.kind = 'support' and s.member_id = t.member_id and s.body ilike v_like escape '\'
			) x order by x.sent_at desc limit 1
		) hit on true
		where hit.body is not null or t.with_name ilike v_like escape '\'
		order by t.kind, t.coach_id, t.member_id
	) r
	order by r.sent_at desc nulls last
	limit 30;
end;
$$;

revoke all on function public.chat_threads(), public.chat_messages(text, uuid, uuid, bigint, integer),
	public.mark_chat_read(text, uuid, uuid), public.mark_chats_delivered(), public.send_support_message(uuid, text),
	public.claim_support_thread(uuid, boolean), public.set_support_status(uuid, boolean), public.chat_search(text)
	from public, anon, authenticated;
grant execute on function public.chat_threads(), public.chat_messages(text, uuid, uuid, bigint, integer),
	public.mark_chat_read(text, uuid, uuid), public.mark_chats_delivered(), public.send_support_message(uuid, text),
	public.claim_support_thread(uuid, boolean), public.set_support_status(uuid, boolean), public.chat_search(text)
	to authenticated;
