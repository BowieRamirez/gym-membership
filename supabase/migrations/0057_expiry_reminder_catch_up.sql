-- Owner, 2026-11-11: the 7-day note in Announcements (0051, F10) reached nobody who already had less than a week left, because the
-- nightly job only matched the one day exactly 7 days before the end (and the 3-day alert only the day 3 days before). Now each
-- reminder catches up: a running term ending within its window that hasn't had it gets it once, worded with the time really left.
-- The note stays the member's alone (recipient_id; read only through my_announcements() and chat_messages, as in 0051).
-- Same signature, so create or replace keeps the grants (none for anon or authenticated, 0008).
create or replace function public.send_expiry_reminders() returns int
language plpgsql security definer set search_path='' as $$
declare v_term record; v_count int:=0; v_id bigint; v_when text; v_today date := (now() at time zone 'Asia/Manila')::date;
begin
  -- 3 days or less: an alert (first, so a member told both at once gets one alert, not two)
  for v_term in select t.*, (t.ends_at at time zone 'Asia/Manila')::date - v_today as days from public.membership_terms t
    where t.cancelled_at is null and t.starts_at<=now() and t.ends_at>now()
    and (t.ends_at at time zone 'Asia/Manila')::date <= v_today+3
    and not exists(select 1 from public.membership_terms n where n.member_id=t.member_id and n.cancelled_at is null
      and n.starts_at<=t.ends_at and n.ends_at>t.ends_at)
    and not exists(select 1 from public.user_notifications n where n.user_id=t.member_id and n.type='membership'
      and n.reference @> jsonb_build_object('term_id',t.term_id,'reminder','3d'))
  loop
    v_when := case v_term.days when 0 then 'today' when 1 then 'tomorrow' else 'in '||v_term.days||' days' end;
    perform public.notify(v_term.member_id,'membership','Your membership ends '||v_when,null,
      jsonb_build_object('term_id',v_term.term_id,'reminder','3d'));
    v_count:=v_count+1;
  end loop;
  -- a week or less: the personal note in Announcements, with an alert unless the 3-day one was just sent
  for v_term in select t.*, (t.ends_at at time zone 'Asia/Manila')::date - v_today as days from public.membership_terms t
    where t.cancelled_at is null and t.starts_at<=now() and t.ends_at>now()
    and (t.ends_at at time zone 'Asia/Manila')::date <= v_today+7
    and not exists(select 1 from public.membership_terms n where n.member_id=t.member_id and n.cancelled_at is null
      and n.starts_at<=t.ends_at and n.ends_at>t.ends_at)
    and not exists(select 1 from public.announcements a where a.recipient_id=t.member_id
      and a.reference @> jsonb_build_object('term_id',t.term_id,'reminder','7d'))
  loop
    v_when := case v_term.days when 0 then 'today' when 1 then 'tomorrow' when 7 then 'in 1 week' else 'in '||v_term.days||' days' end;
    insert into public.announcements (recipient_id, body, reference)
      values (v_term.member_id, 'Your membership ends '||v_when||', on '
        || to_char(v_term.ends_at at time zone 'Asia/Manila', 'Dy FMDD Mon YYYY') || '. Renew from Packages to keep training without a gap.',
        jsonb_build_object('term_id',v_term.term_id,'reminder','7d'))
      returning announcement_id into v_id;
    if v_term.days > 3 then
      perform public.notify(v_term.member_id,'membership','Your membership ends '||v_when,null,
        jsonb_build_object('term_id',v_term.term_id,'reminder','7d','announcement_id',v_id));
    end if;
    v_count:=v_count+1;
  end loop;
  return v_count;
end $$;
