-- Phase 5 (D6, 2026-10-22): one opening and closing time for every day (Asia/Manila), set in Gym settings.
-- A coach's posted shifts are their opened availability (one calendar, not a second one): a shift sits on the
-- 30-minute grid inside gym hours on a single Manila day, and cannot be cancelled, moved or deleted so that a
-- scheduled session of that coach is left outside it. Existing shifts are not re-checked until they are changed.

alter table public.gym_settings
  add column opens_at time not null default '06:00',
  add column closes_at time not null default '22:00',
  add constraint gym_hours_order check (closes_at > opens_at),
  add constraint gym_hours_grid check (extract(minute from opens_at) in (0,30) and extract(minute from closes_at) in (0,30)
    and extract(second from opens_at) = 0 and extract(second from closes_at) = 0);

create function public.guard_shift_post() returns trigger
language plpgsql security definer set search_path='' as $$
declare v_open time; v_close time; v_day date;
begin
  if tg_op <> 'DELETE' and new.status = 'scheduled'
     and (tg_op = 'INSERT' or new.starts_at <> old.starts_at or new.ends_at <> old.ends_at or old.status <> 'scheduled') then
    if extract(epoch from new.starts_at)::bigint % 1800 <> 0 or extract(epoch from new.ends_at)::bigint % 1800 <> 0 then
      raise exception 'off_grid' using errcode='22023';
    end if;
    select opens_at, closes_at into v_open, v_close from public.gym_settings where id = 1;
    v_day := (new.starts_at at time zone 'Asia/Manila')::date;
    if new.starts_at < (v_day + v_open) at time zone 'Asia/Manila' or new.ends_at > (v_day + v_close) at time zone 'Asia/Manila' then
      raise exception 'outside_gym_hours' using errcode='22023';
    end if;
  end if;
  -- a scheduled session inside the old shift must stay inside the shift as it will be
  if tg_op <> 'INSERT' and old.status = 'scheduled' and exists (
    select 1 from public.training_sessions t
    where t.coach_id = old.coach_id and t.status = 'scheduled'
      and tstzrange(t.scheduled_start, t.scheduled_end) && tstzrange(old.starts_at, old.ends_at)
      and (tg_op = 'DELETE' or new.status <> 'scheduled' or not tstzrange(new.starts_at, new.ends_at) @> tstzrange(t.scheduled_start, t.scheduled_end))
  ) then
    raise exception 'shift_has_sessions' using errcode='P0002';
  end if;
  return case when tg_op = 'DELETE' then old else new end;
end $$;
revoke all on function public.guard_shift_post() from public,anon,authenticated;
create trigger shift_posts_guard before insert or update or delete on public.shift_posts for each row execute function public.guard_shift_post();
