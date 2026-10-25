-- Phase 9 (D10, 2026-10-23).
-- A shift cannot be posted, or moved, to a start that has already passed. Overlapping shifts of one coach are already
-- refused by shift_posts_no_overlap (0002); hours and grid by guard_shift_post (0028).
create function public.guard_shift_not_past() returns trigger
language plpgsql security definer set search_path='' as $$
begin
  if new.status = 'scheduled' and (tg_op = 'INSERT' or new.starts_at <> old.starts_at or old.status <> 'scheduled')
     and new.starts_at < now() then
    raise exception 'past_slot' using errcode='22023';
  end if;
  return new;
end $$;
revoke all on function public.guard_shift_not_past() from public,anon,authenticated;
create trigger shift_posts_not_past before insert or update on public.shift_posts for each row execute function public.guard_shift_not_past();

-- An hourly rate is a positive peso amount (numeric(12,2) already caps it at two decimals). A rate of 0 meant "not set",
-- the same as empty: it becomes empty, so both show "Rate not set" and the coach sets a real rate on their next profile save.
update public.coaches set hourly_rate = null where hourly_rate = 0;
alter table public.coaches add constraint coaches_rate_positive check (hourly_rate is null or hourly_rate > 0);
