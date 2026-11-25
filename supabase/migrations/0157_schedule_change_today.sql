-- Owner fixes 2026-11-25, Fix 3 (owner chose option b): a schedule change that starts today (an assignment from today, a change for
-- today) also rewrites today's coach shift, so a coach given today off can't be booked later today. Today's new shift starts no earlier
-- than the next half hour; a shift with a booked session or an amenity booking with the coach is still kept and its day reported.
-- Template edits and the daily fill still start tomorrow (schedule_sync_many's default). As 0156 otherwise.
create or replace function public.schedule_sync_coach(p_user uuid, p_from date, p_to date) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare v_day date; v_s timestamptz; v_e timestamptz; v_post record; v_kept jsonb := '[]'; v_blocked boolean;
  v_today date := (now() at time zone 'Asia/Manila')::date;
  v_next timestamptz := to_timestamp(ceil(extract(epoch from now()) / 1800) * 1800);   -- the next half hour
begin
  if not exists (select 1 from public.user_roles ur join public.roles r on r.role_id = ur.role_id where ur.user_id = p_user and r.name = 'coach') then
    return v_kept;
  end if;
  for v_day in select generate_series(greatest(p_from, v_today), p_to, interval '1 day')::date loop
    v_s := null; v_e := null;
    select f.starts_at, f.ends_at into v_s, v_e from public.shift_for(p_user, v_day) f;
    if (select count(*) from public.shift_posts sp where sp.coach_id = p_user and sp.status = 'scheduled'
        and (sp.starts_at at time zone 'Asia/Manila')::date = v_day) = 1
       and exists (select 1 from public.shift_posts sp where sp.coach_id = p_user and sp.status = 'scheduled'
                   and sp.starts_at = v_s and sp.ends_at = v_e) then
      continue;   -- already right
    end if;
    v_blocked := false;
    for v_post in select shift_post_id, starts_at, ends_at from public.shift_posts sp where sp.coach_id = p_user and sp.status = 'scheduled'
                  and (sp.starts_at at time zone 'Asia/Manila')::date = v_day and sp.ends_at > now() loop   -- a shift already over stays as history
      if exists (select 1 from public.reservations r where r.coach_id = p_user and r.status in ('confirmed', 'awaiting_payment')
                 and r.ends_at > now() and tstzrange(r.starts_at, r.ends_at) && tstzrange(v_post.starts_at, v_post.ends_at)) then
        v_blocked := true;
        continue;
      end if;
      begin
        update public.shift_posts set status = 'cancelled' where shift_post_id = v_post.shift_post_id;
      exception when sqlstate 'P0002' then v_blocked := true;   -- shift_has_sessions
      end;
    end loop;
    if v_day = v_today and v_s is not null then v_s := greatest(v_s, v_next); end if;   -- shifts can't start in the past (0033)
    if v_blocked then v_kept := v_kept || to_jsonb(v_day::text);
    elsif v_s is not null and v_s < v_e then insert into public.shift_posts (coach_id, starts_at, ends_at) values (p_user, v_s, v_e);
    end if;
  end loop;
  return v_kept;
end $$;
revoke all on function public.schedule_sync_coach(uuid, date, date) from public, anon, authenticated;
