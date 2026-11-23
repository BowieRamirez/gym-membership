-- Phase 11 (E8): Hiring and Time requests removed; messages follow sessions.
-- Keep coach_hires and time_requests tables and rows for history; old active hires end with the member's term.

-- 1. Drop hiring and time-request RPCs
drop function if exists public.hire_coach(uuid);
drop function if exists public.answer_hire(bigint, boolean);
drop function if exists public.cancel_hire_request(bigint);
drop function if exists public.request_time(uuid, timestamptz, timestamptz, text);
drop function if exists public.respond_time_request(bigint, boolean);
drop function if exists public.request_member_time(uuid, timestamptz, timestamptz);
drop function if exists public.request_session(uuid, timestamptz, timestamptz, bigint, text);
drop function if exists public.propose_time(bigint, timestamptz, timestamptz, bigint, text);
drop function if exists public.accept_proposal(bigint, integer);
drop function if exists public.request_another_time(bigint, timestamptz, timestamptz, bigint, text);
drop function if exists public.decline_time_request(bigint, text);
drop function if exists public.cancel_time_request(bigint);

-- 2. Messaging rule: open while a session is today or upcoming, or ended in the last 7 days, or an old hire is active
create or replace function public.can_message_coach_member(p_coach uuid, p_member uuid) returns boolean
language sql stable security definer
set search_path = ''
as $$
  select (
    (public.coaches_member(p_coach, p_member) and public.is_active_member(p_member))
    or exists (
      select 1 from public.training_sessions ts
      where ts.coach_id = p_coach
        and ts.member_id = p_member
        and ts.status in ('scheduled', 'completed')
        and (ts.held_until is null or ts.held_until > now())
        and (
          (ts.scheduled_start at time zone 'Asia/Manila')::date >= (now() at time zone 'Asia/Manila')::date
          or ts.scheduled_end >= now() - interval '7 days'
        )
    )
  );
$$;
revoke all on function public.can_message_coach_member(uuid, uuid) from public, anon, authenticated;
grant execute on function public.can_message_coach_member(uuid, uuid) to authenticated;

drop policy if exists messages_insert on public.messages;
create policy messages_insert on public.messages for insert to authenticated
  with check (
    public.is_active_user()
    and sender_id = auth.uid()
    and public.can_message_coach_member(coach_id, member_id)
  );

-- Allow coaches and members who share a training session to read each other's profile (and coach to read member row)
drop policy if exists profiles_select on public.profiles;
create policy profiles_select on public.profiles for select to authenticated
  using (
    public.is_active_user() and (
      public.is_staff()
      or exists (select 1 from public.coaches c where c.user_id = profiles.id)
      or exists (
        select 1 from public.coach_hires h
        where (h.coach_id = auth.uid() and h.member_id = profiles.id)
           or (h.member_id = auth.uid() and h.coach_id = profiles.id)
      )
      or exists (
        select 1 from public.training_sessions ts
        where (ts.coach_id = auth.uid() and ts.member_id = profiles.id)
           or (ts.member_id = auth.uid() and ts.coach_id = profiles.id)
      )
    )
  );

drop policy if exists members_select on public.members;
create policy members_select on public.members for select to authenticated
  using (
    public.is_active_user() and (
      user_id = auth.uid()
      or public.is_staff()
      or exists (select 1 from public.coach_hires h where h.coach_id = auth.uid() and h.member_id = members.user_id)
      or exists (select 1 from public.training_sessions ts where ts.coach_id = auth.uid() and ts.member_id = members.user_id)
    )
  );

create or replace function public.can_use_chat_file(p_name text, p_write boolean) returns boolean
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
    return v_member = v_uid or public.can_answer_queue('front_desk');
  elsif p_name ~ ('^support/(admin|system_admin)/' || v_id || '/' || v_file) then
    v_member := split_part(p_name, '/', 3)::uuid;
    return v_member = v_uid or public.can_answer_queue(split_part(p_name, '/', 2));
  elsif p_name ~ ('^' || v_id || '/' || v_id || '/' || v_file) then
    v_coach := split_part(p_name, '/', 1)::uuid;
    v_member := split_part(p_name, '/', 2)::uuid;
    if v_uid <> v_coach and v_uid <> v_member then return false; end if;
    return not p_write or public.can_message_coach_member(v_coach, v_member);
  end if;
  return false;
end;
$$;
revoke all on function public.can_use_chat_file(text, boolean) from public, anon, authenticated;
grant execute on function public.can_use_chat_file(text, boolean) to authenticated;

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
    -- coach chats: active/ended hires, scheduled/completed sessions, or existing message history
    select 'coach'::text as kind, p.coach_id as coach_id, p.member_id as member_id,
           case when p.coach_id = v_uid then p.member_id else p.coach_id end as with_id,
           pr.full_name as with_name, pr.avatar_path as with_avatar,
           coalesce(nullif(btrim(lm.body), ''), case when lm.attachment_path is not null then 'Photo' end) as last_body,
           lm.sent_at as last_at, coalesce(lm.sender_id = v_uid, false) as last_from_me,
           (select count(*)::integer from public.messages x
             where x.coach_id = p.coach_id and x.member_id = p.member_id and x.sender_id <> v_uid and x.read_at is null) as unread,
           public.can_message_coach_member(p.coach_id, p.member_id) as can_send,
           null::text as status, null::uuid as claimed_by, null::text as claimed_by_name,
           case when public.can_message_coach_member(p.coach_id, p.member_id)
                then (select ls.seen_at from public.user_last_seen ls where ls.user_id = case when p.coach_id = v_uid then p.member_id else p.coach_id end)
           end as with_last_seen,
           null::text as queue
    from (
      select h.coach_id, h.member_id from public.coach_hires h where (h.coach_id = v_uid or h.member_id = v_uid) and h.status in ('active', 'ended')
      union
      select ts.coach_id, ts.member_id from public.training_sessions ts
      where (ts.coach_id = v_uid or ts.member_id = v_uid) and ts.status in ('scheduled', 'completed') and (ts.held_until is null or ts.held_until > now())
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
    from (select * from public.support_threads st where public.can_answer_queue(st.queue) and st.member_id <> v_uid
          order by st.last_message_at desc limit 500) t
    join public.profiles pr on pr.id = t.member_id
    left join public.profiles cb on cb.id = t.claimed_by
    left join lateral (select s.body, s.attachment_path, s.sent_at, s.sender_id from public.support_messages s
                       where s.member_id = t.member_id and s.queue = t.queue order by s.message_id desc limit 1) lm on true

    union all

    -- announcements, for members and coaches
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
revoke all on function public.chat_threads() from public, anon, authenticated;
grant execute on function public.chat_threads() to authenticated;

-- 3. Old active hires end when the member's current term ends
create or replace function public.expire_memberships() returns int
language plpgsql security definer set search_path = '' as $$
declare v_member record; v_count int := 0;
begin
  for v_member in select m.user_id from public.members m
    where exists(select 1 from public.membership_terms t where t.member_id = m.user_id) for update of m
  loop
    if not public.is_active_member(v_member.user_id) and exists(select 1 from public.user_membership_packages
      where member_id = v_member.user_id and status = 'active') then
      perform public.notify(v_member.user_id, 'membership', 'Your membership has expired');
      v_count := v_count + 1;
    end if;
    perform public.project_membership_terms(v_member.user_id);
  end loop;
  update public.user_amenities set status = 'expired' where status = 'active' and expires_at is not null and expires_at <= now();
  update public.coach_hires set status = 'ended', ended_at = now()
  where status = 'active' and not public.is_active_member(member_id);
  return v_count;
end $$;
revoke all on function public.expire_memberships() from public, anon, authenticated;
