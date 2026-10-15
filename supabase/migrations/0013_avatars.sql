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

-- exactly {own uuid}/{file name}: one level, a name that cannot start with a dot, so no "..", no nesting, no other folder
create policy "avatars: add to own folder" on storage.objects for insert to authenticated
	with check (bucket_id = 'avatars' and public.is_active_user()
	            and name ~ ('^' || auth.uid()::text || '/[A-Za-z0-9_-][A-Za-z0-9._-]{0,119}$'));
create policy "avatars: signed-in read" on storage.objects for select to authenticated
	using (bucket_id = 'avatars' and public.is_active_user());

create function public.set_my_avatar(p_path text) returns void
language plpgsql security definer
set search_path = ''
as $$
begin
	perform public.require_active_user();
	p_path := nullif(btrim(p_path), '');
	-- the same shape the storage policy allows: {own uuid}/{file name}; null clears the picture
	if p_path is not null and p_path !~ ('^' || auth.uid()::text || '/[A-Za-z0-9_-][A-Za-z0-9._-]{0,119}$') then
		perform public.fail('forbidden', '42501');
	end if;
	update public.profiles set avatar_path = p_path where id = auth.uid();
end;
$$;

revoke all on function public.set_my_avatar(text) from public, anon, authenticated;
grant execute on function public.set_my_avatar(text) to authenticated;
