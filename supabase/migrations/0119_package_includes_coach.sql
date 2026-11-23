-- Phase 9 (E3, E4, E12, 2026-11-21): Coach included switch and per-package add-on price; coach booking limits in gym_settings.

alter table public.membership_packages
  add column includes_coach boolean not null default false,
  add column coach_addon_price numeric(12,2) check (coach_addon_price > 0),
  add constraint membership_packages_coach_addon_check check (not (includes_coach and coach_addon_price is not null));

do $$
declare
  v_def text := pg_get_functiondef('public.new_package_version(bigint)'::regprocedure);
  v_cols_old text := 'insert into public.membership_packages (name, description, price, duration_days, benefits, image_path, replaces_id, published_at, is_day_pass)';
  v_vals_old text := 'values (v.name, v.description, v.price, v.duration_days, v.benefits, v.image_path, p_id, null, v.is_day_pass)';
begin
  if array_length(string_to_array(v_def, v_cols_old), 1) <> 2 or array_length(string_to_array(v_def, v_vals_old), 1) <> 2 then
    raise exception '0119: new_package_version lacks expected columns';
  end if;
  v_def := replace(v_def, v_cols_old,
    'insert into public.membership_packages (name, description, price, duration_days, benefits, image_path, replaces_id, published_at, is_day_pass, includes_coach, coach_addon_price)');
  v_def := replace(v_def, v_vals_old,
    'values (v.name, v.description, v.price, v.duration_days, v.benefits, v.image_path, p_id, null, v.is_day_pass, v.includes_coach, v.coach_addon_price)');
  execute v_def;
end $$;

revoke all on function public.new_package_version(bigint) from public, anon, authenticated;
grant execute on function public.new_package_version(bigint) to authenticated;

alter table public.gym_settings
  add column max_coach_sessions_per_day int not null default 1 check (max_coach_sessions_per_day between 1 and 20),
  add column max_upcoming_coach_sessions int not null default 2 check (max_upcoming_coach_sessions between 1 and 50),
  add column max_coach_hours_per_day int default 6 check (max_coach_hours_per_day is null or max_coach_hours_per_day between 1 and 24);
