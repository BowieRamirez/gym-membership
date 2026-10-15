-- 0013: profile pictures and catalog images (role redesign, phase 1)
--
-- A picture lives in the avatars bucket under {user uuid}/..., and profiles.avatar_path remembers which file.
-- The app sets it through set_my_avatar (profiles only allows full_name and phone to be updated directly).
-- Package and amenity images are added here too; they are stored in the existing gym-assets bucket and used by later phases.

alter table public.profiles            add column avatar_path text;
alter table public.amenities           add column image_path  text;
alter table public.membership_packages add column image_path  text;

comment on column public.profiles.avatar_path is 'Path in the avatars bucket, {user uuid}/...; null shows initials';
comment on column public.amenities.image_path is 'Path in the gym-assets bucket';
comment on column public.membership_packages.image_path is 'Path in the gym-assets bucket';

insert into storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
values ('avatars', 'avatars', false, 5242880, array['image/png', 'image/jpeg', 'image/webp'])
on conflict (id) do update
set public = excluded.public, file_size_limit = excluded.file_size_limit, allowed_mime_types = excluded.allowed_mime_types;

create policy "avatars: add to own folder" on storage.objects for insert to authenticated
	with check (bucket_id = 'avatars' and public.is_active_user() and (storage.foldername(name))[1] = auth.uid()::text);
create policy "avatars: signed-in read" on storage.objects for select to authenticated
	using (bucket_id = 'avatars' and public.is_active_user());

create function public.set_my_avatar(p_path text) returns void
language plpgsql security definer
set search_path = ''
as $$
begin
	perform public.require_active_user();
	if nullif(btrim(p_path), '') is not null and split_part(btrim(p_path), '/', 1) <> auth.uid()::text then
		perform public.fail('forbidden', '42501');
	end if;
	update public.profiles set avatar_path = nullif(btrim(p_path), '') where id = auth.uid();
end;
$$;

revoke all on function public.set_my_avatar(text) from public, anon, authenticated;
grant execute on function public.set_my_avatar(text) to authenticated;
