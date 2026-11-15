-- G30 (owner, 2026-11-15): Hire sends a request ('pending'); the coach accepts ('active') or declines ('declined'); the member
-- can cancel a waiting request ('cancelled'). Everything that grants access already needs status 'active' (coaches_member,
-- time requests, messages, sessions), so a waiting request unlocks nothing. One open (pending or active) row per member and
-- coach; after a decline or cancel the member may ask again. Archiving either account cancels its waiting requests.

drop index public.coach_hires_one_active;
create unique index coach_hires_one_open on public.coach_hires (member_id, coach_id) where status in ('pending', 'active');

create or replace function public.hire_coach(p_coach_id uuid) returns bigint
language plpgsql security definer
set search_path = ''
as $$
declare
	v_uid uuid := auth.uid();
	v_id bigint;
begin
	perform public.require_active_user();
	if not public.is_active_member(v_uid) then perform public.fail('no_active_membership', '22023'); end if;

	if not exists (
		select 1 from public.coaches c join public.profiles p on p.id = c.user_id
		where c.user_id = p_coach_id and c.is_available and p.is_active
	) then
		perform public.fail('not_found', '22023');
	end if;
	if exists (select 1 from public.coach_hires where member_id = v_uid and coach_id = p_coach_id and status in ('pending', 'active')) then
		perform public.fail('hire_already_requested', '22023');
	end if;

	insert into public.coach_hires (member_id, coach_id, status) values (v_uid, p_coach_id, 'pending')
	returning coach_hire_id into v_id;

	perform public.notify(p_coach_id, 'system', 'New hire request', (select full_name from public.profiles where id = v_uid), jsonb_build_object('member_id', v_uid, 'hire_id', v_id));
	return v_id;
end;
$$;

create function public.answer_hire(p_hire_id bigint, p_accept boolean) returns void
language plpgsql security definer
set search_path = ''
as $$
declare
	v_uid uuid := auth.uid();
	v_hire public.coach_hires;
	v_coach text;
begin
	perform public.require_active_user();
	select * into v_hire from public.coach_hires where coach_hire_id = p_hire_id and coach_id = v_uid for update;
	if not found then perform public.fail('not_found', '22023'); end if;
	if v_hire.status <> 'pending' then perform public.fail('already_processed', 'P0002'); end if;
	v_coach := (select full_name from public.profiles where id = v_uid);

	if p_accept then
		if not public.is_active_member(v_hire.member_id) then perform public.fail('no_active_membership', '22023'); end if;
		update public.coach_hires set status = 'active', hired_at = now() where coach_hire_id = p_hire_id;
		perform public.notify(v_hire.member_id, 'system', 'Hire accepted', v_coach, jsonb_build_object('coach_id', v_uid, 'hire_id', p_hire_id));
	else
		update public.coach_hires set status = 'declined', ended_at = now() where coach_hire_id = p_hire_id;
		perform public.notify(v_hire.member_id, 'system', 'Hire declined', v_coach, jsonb_build_object('coach_id', v_uid, 'hire_id', p_hire_id));
	end if;
end;
$$;

create function public.cancel_hire_request(p_hire_id bigint) returns void
language plpgsql security definer
set search_path = ''
as $$
declare v_hire public.coach_hires;
begin
	perform public.require_active_user();
	select * into v_hire from public.coach_hires where coach_hire_id = p_hire_id and member_id = auth.uid() for update;
	if not found then perform public.fail('not_found', '22023'); end if;
	if v_hire.status <> 'pending' then perform public.fail('already_processed', 'P0002'); end if;
	update public.coach_hires set status = 'cancelled', ended_at = now() where coach_hire_id = p_hire_id;
end;
$$;

revoke all on function public.answer_hire(bigint, boolean) from public, anon;
revoke all on function public.cancel_hire_request(bigint) from public, anon;
grant execute on function public.answer_hire(bigint, boolean) to authenticated;
grant execute on function public.cancel_hire_request(bigint) to authenticated;

-- an archived account's waiting requests are cancelled (they never blocked archiving)
create function public.cancel_requests_of_archived() returns trigger
language plpgsql security definer set search_path = '' as $$
begin
	update public.coach_hires set status = 'cancelled', ended_at = now()
	where status = 'pending' and (member_id = new.id or coach_id = new.id);
	return new;
end $$;
revoke all on function public.cancel_requests_of_archived() from public, anon, authenticated;
create trigger profiles_archived_cancel_requests after update of is_active on public.profiles
	for each row when (old.is_active and not new.is_active) execute function public.cancel_requests_of_archived();

-- A conversation opens only from a hire the coach accepted (active, or ended later): a waiting, declined or cancelled
-- request is not a conversation. Copied from 0051 with that one condition added.
create or replace function public.chat_threads()
returns table (kind text, coach_id uuid, member_id uuid, with_id uuid, with_name text, with_avatar text, last_body text, last_at timestamptz,
               last_from_me boolean, unread integer, can_send boolean, status text, claimed_by uuid, claimed_by_name text, with_last_seen timestamptz,
               queue text)
language plpgsql stable security definer
set search_path = ''
as $$
#variable_conflict use_column
declare
  v_uid uuid := auth.uid();
  v_writer boolean := public.can_write_in(auth.uid());
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
           end as with_last_seen,
           null::text as queue
    from (
      select h.coach_id, h.member_id from public.coach_hires h where (h.coach_id = v_uid or h.member_id = v_uid) and h.status in ('active', 'ended')
      union
      select m.coach_id, m.member_id from public.messages m where m.coach_id = v_uid or m.member_id = v_uid
    ) p
    join public.profiles pr on pr.id = case when p.coach_id = v_uid then p.member_id else p.coach_id end
    left join lateral (select x.body, x.attachment_path, x.sent_at, x.sender_id from public.messages x
                       where x.coach_id = p.coach_id and x.member_id = p.member_id order by x.message_id desc limit 1) lm on true

    union all

    -- the caller's own threads with the gym: Front desk always, Admin and System admin once started
    select 'support'::text, null::uuid, v_uid, null::uuid, public.queue_name(q.queue), null::text,
           coalesce(nullif(btrim(lm.body), ''), case when lm.attachment_path is not null then 'Photo' end), lm.sent_at, coalesce(lm.sender_id = v_uid, false),
           (select count(*)::integer from public.support_messages s where s.member_id = v_uid and s.queue = q.queue and s.sender_id <> v_uid and s.read_at is null),
           true, coalesce(t.status, 'open'), null::uuid, null::text, null::timestamptz, q.queue
    from (select 'front_desk'::text as queue where v_writer
          union select st.queue from public.support_threads st where st.member_id = v_uid and v_writer) q
    left join public.support_threads t on t.member_id = v_uid and t.queue = q.queue
    left join lateral (select s.body, s.attachment_path, s.sent_at, s.sender_id from public.support_messages s
                       where s.member_id = v_uid and s.queue = q.queue order by s.message_id desc limit 1) lm on true

    union all

    -- the inbox: threads in the queues the caller answers
    select 'support'::text, null::uuid, t.member_id, t.member_id, pr.full_name, pr.avatar_path,
           coalesce(nullif(btrim(lm.body), ''), case when lm.attachment_path is not null then 'Photo' end), lm.sent_at, coalesce(lm.sender_id <> t.member_id, false),
           (select count(*)::integer from public.support_messages s where s.member_id = t.member_id and s.queue = t.queue and s.sender_id = t.member_id and s.read_at is null),
           true, t.status, t.claimed_by, cb.full_name,
           (select ls.seen_at from public.user_last_seen ls where ls.user_id = t.member_id),
           t.queue
    -- the 500 most recently active threads, so the list stays small however many people have written
    from (select * from public.support_threads st where public.can_answer_queue(st.queue) and st.member_id <> v_uid
          order by st.last_message_at desc limit 500) t
    join public.profiles pr on pr.id = t.member_id
    left join public.profiles cb on cb.id = t.claimed_by
    left join lateral (select s.body, s.attachment_path, s.sent_at, s.sender_id from public.support_messages s
                       where s.member_id = t.member_id and s.queue = t.queue order by s.message_id desc limit 1) lm on true

    union all

    -- F10: announcements, for members and coaches
    select 'announcements'::text, null::uuid, v_uid, null::uuid, 'Announcements'::text, null::text,
           la.body, la.posted_at, false,
           (select count(*)::integer from public.my_announcements() a where a.announcement_id > coalesce(
              (select r.read_through from public.announcement_reads r where r.user_id = v_uid), 0)),
           false, null::text, null::uuid, null::text, null::timestamptz, null::text
    from (select 1) one
    left join lateral (select a.body, a.posted_at from public.my_announcements() a order by a.announcement_id desc limit 1) la on true
    where v_writer
  ) q
  order by q.last_at desc nulls last;
end;
$$;
