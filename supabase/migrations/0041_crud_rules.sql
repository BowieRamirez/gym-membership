-- Item 13 (2026-11-05, owner decisions D18-D20): tighten what the database allowed but the app never offered.

-- D18: phone and goals have no screen, so nobody needs to edit them. What is already stored stays.
revoke update (phone) on public.profiles from authenticated;
revoke update (goals) on public.members from authenticated;

-- D20: a cancelled shift and an old notification are history. A shift is cancelled, a notification marked read; neither is erased.
revoke delete on public.shift_posts, public.user_notifications from authenticated;
drop policy shift_posts_delete on public.shift_posts;
drop policy notifications_delete on public.user_notifications;

-- D19: a code's end date, limit and on/off can change, but its code and percent can't, because people may have used it at that value.
-- A trigger rather than column rights: installed apps send every column back on save, so unchanged values must keep working.
create function public.guard_discount_fixed() returns trigger
language plpgsql set search_path='' as $$
begin
  if upper(new.code) is distinct from upper(old.code) or new.percent is distinct from old.percent then
    raise exception 'discount_fixed' using errcode='P0002';
  end if;
  return new;
end $$;
revoke all on function public.guard_discount_fixed() from public, anon, authenticated;
create trigger discounts_fixed_guard before update of code, percent on public.discounts
  for each row execute function public.guard_discount_fixed();
