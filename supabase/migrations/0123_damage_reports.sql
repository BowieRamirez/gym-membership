-- Phase 13 (E10): Damage reports after a session or from the desk, reviewed by admins (no automatic charge).

create table if not exists public.damage_reports (
  damage_report_id    bigint generated always as identity primary key,
  training_session_id bigint references public.training_sessions (training_session_id) on delete set null,
  amenity_id          bigint references public.amenities (amenity_id) on delete set null,
  coach_id            uuid references public.profiles (id),
  reported_by         uuid not null references public.profiles (id),
  description         text not null check (char_length(btrim(description)) between 1 and 1000),
  photo_path          text,
  status              text not null default 'open' check (status in ('open', 'reviewed', 'closed')),
  review_note         text,
  reviewed_by         uuid references public.profiles (id),
  reviewed_at         timestamptz,
  created_at          timestamptz not null default now()
);

create index if not exists damage_reports_status_idx on public.damage_reports (status, created_at desc);

alter table public.damage_reports enable row level security;

revoke all on public.damage_reports from public, anon;
grant select, insert, update on public.damage_reports to authenticated;

drop policy if exists damage_reports_select on public.damage_reports;
create policy damage_reports_select on public.damage_reports for select to authenticated
  using (
    public.is_active_user() and (
      public.has_role('admin')
      or public.has_role('subadmin')
      or reported_by = auth.uid()
      or coach_id = auth.uid()
    )
  );

drop policy if exists damage_reports_insert on public.damage_reports;
create policy damage_reports_insert on public.damage_reports for insert to authenticated
  with check (
    public.is_active_user()
    and reported_by = auth.uid()
    and (
      public.is_staff()
      or (
        training_session_id is not null
        and exists (
          select 1 from public.training_sessions ts
          where ts.training_session_id = damage_reports.training_session_id
            and ts.coach_id = auth.uid()
        )
      )
    )
  );

drop policy if exists damage_reports_update on public.damage_reports;
create policy damage_reports_update on public.damage_reports for update to authenticated
  using (public.is_active_user() and (public.has_role('admin') or public.has_role('subadmin')))
  with check (public.has_role('admin') or public.has_role('subadmin'));

create or replace function public.report_damage(
  p_session_id bigint default null,
  p_amenity_id bigint default null,
  p_description text default null,
  p_photo_path text default null
) returns bigint
language plpgsql security definer
set search_path = ''
as $$
declare
  v_uid uuid := auth.uid();
  v_s public.training_sessions;
  v_coach uuid;
  v_amenity bigint := p_amenity_id;
  v_id bigint;
  v_adm record;
begin
  perform public.require_active_user();
  if nullif(btrim(p_description), '') is null then perform public.fail('bad_input', '22023'); end if;

  if p_session_id is not null then
    select * into v_s from public.training_sessions where training_session_id = p_session_id;
    if not found then perform public.fail('not_found', 'P0002'); end if;
    if not public.is_staff() and v_s.coach_id <> v_uid then perform public.fail('forbidden', '42501'); end if;
    v_coach := v_s.coach_id;
    if v_amenity is null then
      select r.amenity_id into v_amenity from public.reservations r
      where r.training_session_id = p_session_id limit 1;
    end if;
  else
    if not public.is_staff() then perform public.fail('forbidden', '42501'); end if;
  end if;

  insert into public.damage_reports (training_session_id, amenity_id, coach_id, reported_by, description, photo_path)
  values (p_session_id, v_amenity, v_coach, v_uid, btrim(p_description), nullif(btrim(p_photo_path), ''))
  returning damage_report_id into v_id;

  for v_adm in
    select distinct ur.user_id
    from public.user_roles ur
    join public.roles r on r.role_id = ur.role_id
    join public.profiles p on p.id = ur.user_id
    where r.name in ('admin', 'subadmin') and p.is_active
  loop
    perform public.notify(v_adm.user_id, 'system', 'Damage report filed', left(btrim(p_description), 120),
      jsonb_build_object('damage_report_id', v_id));
  end loop;

  perform public.audit('damage_report.create', 'damage_reports', v_id::text,
    jsonb_build_object('session_id', p_session_id, 'amenity_id', v_amenity));
  return v_id;
end $$;

create or replace function public.review_damage(
  p_report_id bigint,
  p_status text,
  p_note text default null,
  p_set_maintenance boolean default false
) returns void
language plpgsql security definer
set search_path = ''
as $$
declare
  v_uid uuid := auth.uid();
  v_r public.damage_reports;
begin
  perform public.require_active_user();
  if not (public.has_role('admin') or public.has_role('subadmin')) then perform public.fail('forbidden', '42501'); end if;
  if p_status not in ('reviewed', 'closed') then perform public.fail('bad_input', '22023'); end if;

  select * into v_r from public.damage_reports where damage_report_id = p_report_id for update;
  if not found then perform public.fail('not_found', 'P0002'); end if;

  update public.damage_reports
  set status = p_status,
      review_note = coalesce(nullif(btrim(p_note), ''), review_note),
      reviewed_by = v_uid,
      reviewed_at = now()
  where damage_report_id = p_report_id;

  if p_set_maintenance and v_r.amenity_id is not null then
    perform public.set_amenity_status(v_r.amenity_id, 'unavailable', coalesce(nullif(btrim(p_note), ''), 'Maintenance'));
  end if;

  perform public.audit('damage_report.review', 'damage_reports', p_report_id::text,
    jsonb_build_object('status', p_status, 'maintenance', p_set_maintenance));
end $$;

revoke all on function
  public.report_damage(bigint, bigint, text, text),
  public.review_damage(bigint, text, text, boolean)
from public, anon, authenticated;

grant execute on function
  public.report_damage(bigint, bigint, text, text),
  public.review_damage(bigint, text, text, boolean)
to authenticated;
