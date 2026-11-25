-- Owner fixes 2026-11-25, Fix 3 Phase 3.6 (whole-fix review): the schedule kept a coach shift only for a booked coach session (the
-- shift guard, 0028). An amenity booking that includes the coach (reservations.coach_id, 0064) needs the shift too, so it now also keeps
-- the shift and its day is reported, as S8 says. As 0151 otherwise.
create or replace function public.schedule_sync_coach(p_user uuid, p_from date, p_to date) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare v_day date; v_s timestamptz; v_e timestamptz; v_post record; v_kept jsonb := '[]'; v_blocked boolean;
  v_tomorrow date := (now() at time zone 'Asia/Manila')::date + 1;
begin
  if not exists (select 1 from public.user_roles ur join public.roles r on r.role_id = ur.role_id where ur.user_id = p_user and r.name = 'coach') then
    return v_kept;
  end if;
  for v_day in select generate_series(greatest(p_from, v_tomorrow), p_to, interval '1 day')::date loop
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
                  and (sp.starts_at at time zone 'Asia/Manila')::date = v_day loop
      if exists (select 1 from public.reservations r where r.coach_id = p_user and r.status in ('confirmed', 'awaiting_payment')
                 and tstzrange(r.starts_at, r.ends_at) && tstzrange(v_post.starts_at, v_post.ends_at)) then
        v_blocked := true;   -- an amenity booking with this coach needs the shift
        continue;
      end if;
      begin
        update public.shift_posts set status = 'cancelled' where shift_post_id = v_post.shift_post_id;
      exception when sqlstate 'P0002' then v_blocked := true;   -- shift_has_sessions
      end;
    end loop;
    if v_blocked then v_kept := v_kept || to_jsonb(v_day::text);
    elsif v_s is not null then insert into public.shift_posts (coach_id, starts_at, ends_at) values (p_user, v_s, v_e);
    end if;
  end loop;
  return v_kept;
end $$;
revoke all on function public.schedule_sync_coach(uuid, date, date) from public, anon, authenticated;
