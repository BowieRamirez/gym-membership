-- Phase 4 of the 2026-11-09 plan (owner decisions F9, F10, F11, 2026-11-10).
--
-- F9: members and coaches write to a staff queue they choose: Front desk, Admin or System admin. Each queue is shared by its role
--     (anyone in it can answer and claim the conversation). Front desk staff see the Front desk queue; Admin sees Front desk and
--     Admin; System admin sees all three. The existing front-desk threads (support_threads / support_messages) gain a queue, so
--     there is still one chat system; member_id now means "the person who wrote in", a member or a coach.
-- F10: announcements from Admin and System admin, read-only for members and coaches in an "Announcements" conversation, with a
--     notification each; the nightly job also posts a personal note there 7 days before a term ends (the 3-day reminder stays).
-- F11: a time request, an invitation and every answer to them appear in the member-coach chat as a message in the name of whoever
--     acted (kind 'request', linked to the request), made in one place: a trigger on the request history.

-- ---------------------------------------------------------------------
-- F9: queues
-- ---------------------------------------------------------------------

alter table public.support_threads drop constraint support_threads_pkey;
alter table public.support_threads drop constraint support_threads_member_id_fkey;
alter table public.support_threads add column queue text not null default 'front_desk' check (queue in ('front_desk', 'admin', 'system_admin'));
alter table public.support_threads add primary key (member_id, queue);
alter table public.support_threads add constraint support_threads_person_fkey foreign key (member_id) references public.profiles (id) on delete cascade;

alter table public.support_messages drop constraint support_messages_member_id_fkey;
alter table public.support_messages add column queue text not null default 'front_desk' check (queue in ('front_desk', 'admin', 'system_admin'));
alter table public.support_messages add constraint support_messages_person_fkey foreign key (member_id) references public.profiles (id) on delete cascade;
drop index public.support_messages_thread_idx;
create index support_messages_thread_idx on public.support_messages (member_id, queue, message_id desc);

-- a photo lives in its thread's folder: support/{person}/{file} for the Front desk (as before), support/{queue}/{person}/{file} otherwise
create function public.support_folder(p_queue text, p_person uuid) returns text
language sql immutable set search_path = '' as $$
  select 'support/' || case when p_queue = 'front_desk' then '' else p_queue || '/' end || p_person::text;
$$;
alter table public.support_messages drop constraint support_messages_photo_in_thread;
alter table public.support_messages add constraint support_messages_photo_in_thread
  check (attachment_path is null or attachment_path ~ ('^' || public.support_folder(queue, member_id) || '/[A-Za-z0-9_-][A-Za-z0-9._-]{0,119}$'));

-- may the caller answer this queue?
create function public.can_answer_queue(p_queue text) returns boolean
language sql stable security definer set search_path = '' as $$
  select public.is_active_user() and case p_queue
    when 'front_desk' then public.has_permission('support:reply')
    when 'admin' then public.has_role('admin') or public.has_role('subadmin')
    when 'system_admin' then public.has_role('admin')
    else false end;
$$;

-- who may write in to the gym: members and coaches, for themselves
create function public.can_write_in(p_user uuid) returns boolean
language sql stable security definer set search_path = '' as $$
  select exists (select 1 from public.members where user_id = p_user) or exists (select 1 from public.coaches where user_id = p_user);
$$;

create function public.queue_name(p_queue text) returns text
language sql immutable set search_path = '' as $$
  select case p_queue when 'admin' then 'Admin' when 'system_admin' then 'System admin' else 'Front desk' end;
$$;

-- everyone active who answers a queue
create function public.notify_queue(p_queue text, p_title text, p_body text, p_reference jsonb) returns void
language sql security definer set search_path = '' as $$
  insert into public.user_notifications (user_id, type, title, body, reference)
  select distinct ur.user_id, 'message'::public.notification_type, p_title, p_body, p_reference
  from public.user_roles ur join public.roles r using (role_id) join public.profiles pr on pr.id = ur.user_id and pr.is_active
  where r.name = any (case p_queue when 'front_desk' then array['admin', 'subadmin', 'employee']
                                   when 'admin' then array['admin', 'subadmin'] else array['admin'] end);
$$;

drop policy support_threads_select on public.support_threads;
drop policy support_messages_select on public.support_messages;
create policy support_threads_select on public.support_threads for select to authenticated
  using (public.is_active_user() and (member_id = auth.uid() or public.can_answer_queue(queue)));
create policy support_messages_select on public.support_messages for select to authenticated
  using (public.is_active_user() and (member_id = auth.uid() or public.can_answer_queue(queue)));

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
    return not p_write or (public.coaches_member(v_coach, v_member) and public.is_active_member(v_member));
  end if;
  return false;
end;
$$;

-- ---------------------------------------------------------------------
-- F10: announcements
-- ---------------------------------------------------------------------

create table public.announcements (
  announcement_id bigint      generated always as identity primary key,
  author_id       uuid        references public.profiles (id),                       -- null: written by the gym's nightly job
  audience        text        not null default 'everyone' check (audience in ('everyone', 'members', 'coaches')),
  recipient_id    uuid        references public.profiles (id) on delete cascade,    -- set: a personal note to one person
  body            text        not null check (char_length(btrim(body)) between 1 and 2000),
  posted_at       timestamptz not null default now(),
  reference       jsonb                                                               -- what a nightly note is about, so it is sent once
);
create index announcements_recipient on public.announcements (recipient_id, announcement_id desc);
create table public.announcement_reads (
  user_id      uuid   primary key references public.profiles (id) on delete cascade,
  read_through bigint not null default 0
);
alter table public.announcements enable row level security;        -- no grants or policies: read and written through the functions below
alter table public.announcement_reads enable row level security;
revoke all on public.announcements, public.announcement_reads from anon, authenticated;

-- what the caller may read: notes to them, and announcements to an audience they belong to
create function public.my_announcements() returns setof public.announcements
language sql stable security definer set search_path = '' as $$
  select a.* from public.announcements a
  where a.recipient_id = auth.uid()
     or (a.recipient_id is null and (
          (a.audience in ('everyone', 'members') and exists (select 1 from public.members where user_id = auth.uid()))
       or (a.audience in ('everyone', 'coaches') and exists (select 1 from public.coaches where user_id = auth.uid()))));
$$;
revoke all on function public.my_announcements() from public, anon, authenticated;

create function public.post_announcement(p_body text, p_audience text default 'everyone') returns public.announcements
language plpgsql security definer set search_path = '' as $$
declare v public.announcements; v_body text := btrim(coalesce(p_body, ''));
begin
  perform public.require_active_user();
  if not (public.has_role('admin') or public.has_role('subadmin')) then perform public.fail('forbidden', '42501'); end if;
  if char_length(v_body) not between 1 and 2000 or p_audience is null or p_audience not in ('everyone', 'members', 'coaches') then
    perform public.fail('bad_input', '22023');
  end if;
  insert into public.announcements (author_id, audience, body) values (auth.uid(), p_audience, v_body) returning * into v;
  insert into public.user_notifications (user_id, type, title, body, reference)
  select p.id, 'message'::public.notification_type, 'Announcement', left(v_body, 80), jsonb_build_object('announcement_id', v.announcement_id)
  from public.profiles p
  where p.is_active and p.deleted_at is null
    and ((p_audience in ('everyone', 'members') and exists (select 1 from public.members m where m.user_id = p.id))
      or (p_audience in ('everyone', 'coaches') and exists (select 1 from public.coaches c where c.user_id = p.id)));
  perform public.audit('announcement.post', 'announcements', v.announcement_id::text, jsonb_build_object('audience', p_audience));
  return v;
end $$;

-- the staff page: announcements already posted, newest first, with who posted them (personal notes are not listed)
create function public.announcements_list(p_before bigint default null, p_limit integer default 50)
returns table (announcement_id bigint, author_name text, audience text, body text, posted_at timestamptz)
language plpgsql stable security definer set search_path = '' as $$
begin
  perform public.require_active_user();
  if not (public.has_role('admin') or public.has_role('subadmin')) then perform public.fail('forbidden', '42501'); end if;
  return query select a.announcement_id, p.full_name, a.audience, a.body, a.posted_at
    from public.announcements a left join public.profiles p on p.id = a.author_id
    where a.recipient_id is null and (p_before is null or a.announcement_id < p_before)
    order by a.announcement_id desc limit least(greatest(coalesce(p_limit, 50), 1), 100);
end $$;

-- F10: the nightly reminders. 3 days before (as 0018), and now 7 days before with a personal note in Announcements.
create or replace function public.send_expiry_reminders() returns int
language plpgsql security definer set search_path='' as $$
declare v_term record; v_count int:=0; v_id bigint;
begin
  for v_term in select t.* from public.membership_terms t where t.cancelled_at is null and t.starts_at<=now() and t.ends_at>now()
    and (t.ends_at at time zone 'Asia/Manila')::date=(now() at time zone 'Asia/Manila')::date+3
    and not exists(select 1 from public.membership_terms n where n.member_id=t.member_id and n.cancelled_at is null
      and n.starts_at<=t.ends_at and n.ends_at>t.ends_at)
    and not exists(select 1 from public.user_notifications n where n.user_id=t.member_id and n.type='membership'
      and n.reference @> jsonb_build_object('term_id',t.term_id,'reminder','3d'))
  loop
    perform public.notify(v_term.member_id,'membership','Your membership ends in 3 days',null,
      jsonb_build_object('term_id',v_term.term_id,'reminder','3d'));
    v_count:=v_count+1;
  end loop;
  for v_term in select t.* from public.membership_terms t where t.cancelled_at is null and t.starts_at<=now() and t.ends_at>now()
    and (t.ends_at at time zone 'Asia/Manila')::date=(now() at time zone 'Asia/Manila')::date+7
    and not exists(select 1 from public.membership_terms n where n.member_id=t.member_id and n.cancelled_at is null
      and n.starts_at<=t.ends_at and n.ends_at>t.ends_at)
    and not exists(select 1 from public.announcements a where a.recipient_id=t.member_id
      and a.reference @> jsonb_build_object('term_id',t.term_id,'reminder','7d'))
  loop
    insert into public.announcements (recipient_id, body, reference)
      values (v_term.member_id, 'Your membership ends in 1 week, on '
        || to_char(v_term.ends_at at time zone 'Asia/Manila', 'Dy FMDD Mon YYYY') || '. Renew from Packages to keep training without a gap.',
        jsonb_build_object('term_id',v_term.term_id,'reminder','7d'))
      returning announcement_id into v_id;
    perform public.notify(v_term.member_id,'membership','Your membership ends in 1 week',null,
      jsonb_build_object('term_id',v_term.term_id,'reminder','7d','announcement_id',v_id));
    v_count:=v_count+1;
  end loop;
  return v_count;
end $$;

-- ---------------------------------------------------------------------
-- F11: requests in the member-coach chat
-- ---------------------------------------------------------------------

alter table public.messages add column kind text not null default 'text' check (kind in ('text', 'request'));
alter table public.messages add column time_request_id bigint references public.time_requests (time_request_id) on delete set null;
-- the app inserts only (coach_id, member_id, sender_id, body, attachment_path) (0006, 0016), so it can't make a request message

-- an automatic message is not a "New message" alert: the request already sends its own
create or replace function public.notify_new_message() returns trigger
language plpgsql security definer
set search_path = ''
as $$
begin
  if new.kind = 'request' then return new; end if;
  perform public.notify(
    case when new.sender_id = new.coach_id then new.member_id else new.coach_id end,
    'message', 'New message', null,
    jsonb_build_object('coach_id', new.coach_id, 'member_id', new.member_id));
  return new;
end;
$$;

create function public.session_words(p_start timestamptz, p_end timestamptz, p_amenity bigint) returns text
language sql stable security definer set search_path = '' as $$
  select to_char(p_start at time zone 'Asia/Manila', 'Dy FMDD Mon, FMHH12:MI AM') || ' to ' || to_char(p_end at time zone 'Asia/Manila', 'FMHH12:MI AM')
    || coalesce(', at ' || (select name from public.amenities where amenity_id = p_amenity), '');
$$;

-- Deferred to the end of the transaction, so a request made in two steps (request_session sets the amenity after inserting) is
-- described as it ended up. Only the member or the coach of the request speaks; jobs and staff actions post nothing.
create function public.post_request_message() returns trigger
language plpgsql security definer set search_path = '' as $$
declare r public.time_requests; v_body text; d jsonb := new.details;
begin
  select * into r from public.time_requests where time_request_id = new.time_request_id;
  if not found or r.member_id is null or new.actor_id is null or new.actor_id not in (r.coach_id, r.member_id) then return null; end if;
  v_body := case
    when new.from_status is null and r.requested_by = 'member' then
      'I would like to request a training session: ' || public.session_words(r.requested_start, r.requested_end, r.amenity_id) || '.'
        || coalesce(' Note: ' || nullif(btrim(r.message), ''), '')
    when new.from_status is null then
      'I would like to invite you to a training session: ' || public.session_words(r.requested_start, r.requested_end, r.amenity_id) || '.'
        || coalesce(' Note: ' || nullif(btrim(r.message), ''), '')
    when new.to_status = 'approved' and new.from_status = 'proposed' then
      'I accepted the new time: ' || public.session_words((d->>'proposed_start')::timestamptz, (d->>'proposed_end')::timestamptz, (d->>'proposed_amenity')::bigint) || '.'
    when new.to_status = 'approved' then
      'I accepted the training session: ' || public.session_words((d->>'start')::timestamptz, (d->>'end')::timestamptz, (d->>'amenity')::bigint) || '.'
    when new.to_status = 'rejected' then
      'I declined the training session on ' || public.session_words((d->>'start')::timestamptz, (d->>'end')::timestamptz, (d->>'amenity')::bigint) || '.'
        || coalesce(' Reason: ' || nullif(btrim(new.reason), ''), '')
    when new.to_status = 'proposed' then
      'Could we do another time? ' || public.session_words((d->>'proposed_start')::timestamptz, (d->>'proposed_end')::timestamptz, (d->>'proposed_amenity')::bigint) || '.'
        || coalesce(' Reason: ' || nullif(btrim(new.reason), ''), '')
    when new.to_status = 'pending' then
      'Could we do this time instead? ' || public.session_words((d->>'start')::timestamptz, (d->>'end')::timestamptz, (d->>'amenity')::bigint) || '.'
    when new.to_status = 'cancelled' then
      'I cancelled the training session request for ' || public.session_words((d->>'start')::timestamptz, (d->>'end')::timestamptz, (d->>'amenity')::bigint) || '.'
  end;
  if v_body is null then return null; end if;
  insert into public.messages (coach_id, member_id, sender_id, body, kind, time_request_id)
    values (r.coach_id, r.member_id, new.actor_id, left(v_body, 2000), 'request', r.time_request_id);
  return null;
end $$;
revoke all on function public.post_request_message(), public.session_words(timestamptz, timestamptz, bigint) from public, anon, authenticated;
create constraint trigger time_request_events_chat after insert on public.time_request_events
  deferrable initially deferred for each row execute function public.post_request_message();

-- ---------------------------------------------------------------------
-- Reading and sending (replacing 0015-0017)
-- ---------------------------------------------------------------------

-- Same as 0017, plus: a queue column; the person's own threads are "Front desk" (always there) and any Admin or System admin thread
-- they have started, for coaches as well as members; the inbox lists only queues the caller answers; and members and coaches get an
-- "Announcements" conversation (kind 'announcements', read-only).
drop function public.chat_threads();
create function public.chat_threads()
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
      select h.coach_id, h.member_id from public.coach_hires h where h.coach_id = v_uid or h.member_id = v_uid
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

-- Same as 0016, plus p_queue for the gym threads, an 'announcements' kind, and each message's kind and request (F11).
drop function public.chat_messages(text, uuid, uuid, bigint, integer);
create function public.chat_messages(p_kind text, p_member uuid, p_coach uuid default null, p_before bigint default null, p_limit integer default 50,
                                     p_queue text default 'front_desk')
returns table (message_id bigint, sender_id uuid, body text, sent_at timestamptz, delivered_at timestamptz, read_at timestamptz, sender_name text,
               attachment_path text, kind text, time_request_id bigint)
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
      select m.message_id, m.sender_id, m.body, m.sent_at, m.delivered_at, m.read_at, null::text, m.attachment_path, m.kind, m.time_request_id
      from public.messages m
      where m.coach_id = p_coach and m.member_id = p_member and (p_before is null or m.message_id < p_before)
      order by m.message_id desc limit v_limit;
  elsif p_kind = 'support' then
    if v_uid is distinct from p_member and not public.can_answer_queue(p_queue) then perform public.fail('forbidden', '42501'); end if;
    return query
      select s.message_id, s.sender_id, s.body, s.sent_at, s.delivered_at, s.read_at,
             -- the person who wrote in never learns which staff member answered; staff do
             case when v_uid = p_member or s.sender_id = p_member then null else (select pr.full_name from public.profiles pr where pr.id = s.sender_id) end,
             s.attachment_path, 'text'::text, null::bigint
      from public.support_messages s
      where s.member_id = p_member and s.queue = p_queue and (p_before is null or s.message_id < p_before)
      order by s.message_id desc limit v_limit;
  elsif p_kind = 'announcements' then
    if v_uid is distinct from p_member then perform public.fail('forbidden', '42501'); end if;
    return query
      select a.announcement_id, a.author_id, a.body, a.posted_at, a.posted_at, null::timestamptz,
             case when a.recipient_id is null then 'Announcement' else 'For you' end, null::text, 'text'::text, null::bigint
      from public.my_announcements() a
      where p_before is null or a.announcement_id < p_before
      order by a.announcement_id desc limit v_limit;
  else
    perform public.fail('bad_input', '22023');
  end if;
end;
$$;

drop function public.mark_chat_read(text, uuid, uuid);
create function public.mark_chat_read(p_kind text, p_member uuid, p_coach uuid default null, p_queue text default 'front_desk') returns void
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
    if v_uid is distinct from p_member and not public.can_answer_queue(p_queue) then perform public.fail('forbidden', '42501'); end if;
    update public.support_messages
    set delivered_at = coalesce(delivered_at, now()), read_at = coalesce(read_at, now())
    where member_id = p_member and queue = p_queue and read_at is null
      and ((v_uid = p_member and sender_id <> p_member) or (v_uid <> p_member and sender_id = p_member));
    update public.user_notifications set read_at = now()
    where user_id = v_uid and type = 'message' and read_at is null
      and reference @> jsonb_build_object('support_member', p_member::text) and coalesce(reference->>'support_queue', 'front_desk') = p_queue;
  elsif p_kind = 'announcements' then
    if v_uid is distinct from p_member then perform public.fail('forbidden', '42501'); end if;
    insert into public.announcement_reads (user_id, read_through)
      values (v_uid, coalesce((select max(a.announcement_id) from public.my_announcements() a), 0))
      on conflict (user_id) do update set read_through = greatest(public.announcement_reads.read_through, excluded.read_through);
    update public.user_notifications set read_at = now()
    where user_id = v_uid and read_at is null and reference ? 'announcement_id';
  else
    perform public.fail('bad_input', '22023');
  end if;
end;
$$;

create or replace function public.mark_chats_delivered() returns void
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
    and (member_id = v_uid or (sender_id = member_id and public.can_answer_queue(queue)));
end;
$$;

-- A member or coach writes to a queue (p_member is themselves), or staff of that queue answer. The photo is optional.
drop function public.send_support_message(uuid, text, text);
create function public.send_support_message(p_member uuid, p_body text, p_attachment text default null, p_queue text default 'front_desk') returns bigint
language plpgsql security definer
set search_path = ''
as $$
declare
  v_uid uuid := auth.uid();
  v_body text := coalesce(btrim(p_body), '');
  v_att text := nullif(btrim(p_attachment), '');
  v_ref jsonb := jsonb_build_object('support_member', p_member::text, 'support_queue', p_queue);
  v_id bigint;
  v_claimed uuid;
  v_name text;
  v_preview text;
begin
  perform public.require_active_user();
  if p_queue is null or p_queue not in ('front_desk', 'admin', 'system_admin') then perform public.fail('bad_input', '22023'); end if;
  if v_body = '' and v_att is null then perform public.fail('empty_message', '22023'); end if;
  if char_length(v_body) > 2000 then perform public.fail('message_too_long', '22023'); end if;

  if v_uid = p_member then
    if not public.can_write_in(v_uid) then perform public.fail('forbidden', '42501'); end if;
  elsif public.can_answer_queue(p_queue) then
    if not public.can_write_in(p_member) then perform public.fail('not_found', '22023'); end if;
  else
    perform public.fail('forbidden', '42501');
  end if;

  -- the photo has to be in this thread's folder
  if v_att is not null and v_att !~ ('^' || public.support_folder(p_queue, p_member) || '/[A-Za-z0-9_-][A-Za-z0-9._-]{0,119}$') then
    perform public.fail('forbidden', '42501');
  end if;

  insert into public.support_threads (member_id, queue) values (p_member, p_queue) on conflict (member_id, queue) do nothing;
  insert into public.support_messages (member_id, queue, sender_id, body, attachment_path) values (p_member, p_queue, v_uid, v_body, v_att) returning message_id into v_id;
  v_preview := coalesce(nullif(left(v_body, 80), ''), 'Photo');

  if v_uid = p_member then
    update public.support_threads set last_message_at = now(), status = 'open' where member_id = p_member and queue = p_queue returning claimed_by into v_claimed;
    select full_name into v_name from public.profiles where id = v_uid;
    if v_claimed is not null and exists (select 1 from public.profiles where id = v_claimed and is_active) then
      perform public.notify(v_claimed, 'message', 'New message from ' || v_name, v_preview, v_ref);
    else
      perform public.notify_queue(p_queue, 'New message from ' || v_name || case when p_queue = 'front_desk' then '' else ' to ' || public.queue_name(p_queue) end, v_preview, v_ref);
    end if;
  else
    update public.support_threads set last_message_at = now() where member_id = p_member and queue = p_queue;
    perform public.notify(p_member, 'message', 'Message from ' || case when p_queue = 'front_desk' then 'the front desk' else public.queue_name(p_queue) end, v_preview, v_ref);
  end if;

  return v_id;
end;
$$;

-- Take or release a conversation in a queue you answer. Releasing someone else's is for the System admin.
drop function public.claim_support_thread(uuid, boolean);
create function public.claim_support_thread(p_member uuid, p_claim boolean, p_queue text default 'front_desk') returns void
language plpgsql security definer
set search_path = ''
as $$
declare
  v public.support_threads;
begin
  perform public.require_active_user();
  if not public.can_answer_queue(p_queue) then perform public.fail('forbidden', '42501'); end if;
  select * into v from public.support_threads where member_id = p_member and queue = p_queue for update;
  if not found then perform public.fail('not_found', '22023'); end if;

  if p_claim then
    if v.claimed_by is not null and v.claimed_by <> auth.uid()
       and exists (select 1 from public.profiles where id = v.claimed_by and is_active) then perform public.fail('already_processed', 'P0002'); end if;
    update public.support_threads set claimed_by = auth.uid() where member_id = p_member and queue = p_queue;
  else
    if v.claimed_by is not null and v.claimed_by <> auth.uid() and not public.has_role('admin')
       and exists (select 1 from public.profiles where id = v.claimed_by and is_active) then perform public.fail('forbidden', '42501'); end if;
    update public.support_threads set claimed_by = null where member_id = p_member and queue = p_queue;
  end if;
end;
$$;

drop function public.set_support_status(uuid, boolean);
create function public.set_support_status(p_member uuid, p_open boolean, p_queue text default 'front_desk') returns void
language plpgsql security definer
set search_path = ''
as $$
begin
  perform public.require_active_user();
  if not public.can_answer_queue(p_queue) then perform public.fail('forbidden', '42501'); end if;
  update public.support_threads set status = case when p_open then 'open' else 'closed' end where member_id = p_member and queue = p_queue;
  if not found then perform public.fail('not_found', '22023'); end if;
end;
$$;

-- Same as 0015, with the queue: search the other person's name or message text among your own conversations (not Announcements).
drop function public.chat_search(text);
create function public.chat_search(p_query text)
returns table (kind text, coach_id uuid, member_id uuid, with_id uuid, with_name text, snippet text, sent_at timestamptz, queue text)
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
  v_like := '%' || replace(replace(replace(v_q, '\', '\\'), '%', '\%'), '_', '\_') || '%';

  return query
  select * from (
    select distinct on (t.kind, t.coach_id, t.member_id, t.queue)
           t.kind, t.coach_id, t.member_id, t.with_id, t.with_name,
           coalesce(hit.body, t.last_body) as snippet, coalesce(hit.sent_at, t.last_at) as sent_at, t.queue
    from public.chat_threads() t
    left join lateral (
      select x.body, x.sent_at from (
        select m.body, m.sent_at from public.messages m
        where t.kind = 'coach' and m.coach_id = t.coach_id and m.member_id = t.member_id and m.body ilike v_like escape '\'
        union all
        select s.body, s.sent_at from public.support_messages s
        where t.kind = 'support' and s.member_id = t.member_id and s.queue = t.queue and s.body ilike v_like escape '\'
      ) x order by x.sent_at desc limit 1
    ) hit on true
    where t.kind <> 'announcements' and (hit.body is not null or t.with_name ilike v_like escape '\')
    order by t.kind, t.coach_id, t.member_id, t.queue
  ) r
  order by r.sent_at desc nulls last
  limit 30;
end;
$$;

-- can_answer_queue is used by the row policies above, so signed-in users keep execute on it (as is_active_user and has_permission)
revoke all on function public.can_answer_queue(text) from public, anon;
grant execute on function public.can_answer_queue(text) to authenticated;
revoke all on function public.can_write_in(uuid), public.notify_queue(text, text, text, jsonb) from public, anon, authenticated;
revoke all on function public.chat_threads(), public.chat_messages(text, uuid, uuid, bigint, integer, text), public.mark_chat_read(text, uuid, uuid, text),
  public.send_support_message(uuid, text, text, text), public.claim_support_thread(uuid, boolean, text), public.set_support_status(uuid, boolean, text),
  public.chat_search(text), public.post_announcement(text, text), public.announcements_list(bigint, integer) from public, anon, authenticated;
grant execute on function public.chat_threads(), public.chat_messages(text, uuid, uuid, bigint, integer, text), public.mark_chat_read(text, uuid, uuid, text),
  public.send_support_message(uuid, text, text, text), public.claim_support_thread(uuid, boolean, text), public.set_support_status(uuid, boolean, text),
  public.chat_search(text), public.post_announcement(text, text), public.announcements_list(bigint, integer) to authenticated;
