-- 0007: storage buckets and access rules (plan 9)
--
-- Proof photos live under {user uuid}/..., so a member can only add to and read their own
-- folder; staff read everything. Images only, 5 MB at most.

insert into storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
values
	('payment-proofs', 'payment-proofs', false, 5242880, array['image/png', 'image/jpeg', 'image/webp']),
	('amenity-proofs', 'amenity-proofs', false, 5242880, array['image/png', 'image/jpeg', 'image/webp']),
	('gym-assets',     'gym-assets',     false, 5242880, array['image/png', 'image/jpeg', 'image/webp'])
on conflict (id) do update
set public = excluded.public, file_size_limit = excluded.file_size_limit, allowed_mime_types = excluded.allowed_mime_types;

-- payment-proofs
create policy "payment proofs: add to own folder" on storage.objects for insert to authenticated
	with check (bucket_id = 'payment-proofs' and public.is_active_user() and (storage.foldername(name))[1] = auth.uid()::text);
create policy "payment proofs: read own, staff read all" on storage.objects for select to authenticated
	using (bucket_id = 'payment-proofs' and public.is_active_user()
	       and ((storage.foldername(name))[1] = auth.uid()::text or public.is_staff()));

-- amenity-proofs
create policy "amenity proofs: add to own folder" on storage.objects for insert to authenticated
	with check (bucket_id = 'amenity-proofs' and public.is_active_user() and (storage.foldername(name))[1] = auth.uid()::text);
create policy "amenity proofs: read own, staff read all" on storage.objects for select to authenticated
	using (bucket_id = 'amenity-proofs' and public.is_active_user()
	       and ((storage.foldername(name))[1] = auth.uid()::text or public.is_staff()));

-- gym-assets: the payment QR. Everyone signed in reads it, the admin manages it
create policy "gym assets: signed-in read" on storage.objects for select to authenticated
	using (bucket_id = 'gym-assets' and public.is_active_user());
create policy "gym assets: admin add" on storage.objects for insert to authenticated
	with check (bucket_id = 'gym-assets' and public.has_permission('gym_settings:manage'));
create policy "gym assets: admin replace" on storage.objects for update to authenticated
	using (bucket_id = 'gym-assets' and public.has_permission('gym_settings:manage'))
	with check (bucket_id = 'gym-assets' and public.has_permission('gym_settings:manage'));
create policy "gym assets: admin remove" on storage.objects for delete to authenticated
	using (bucket_id = 'gym-assets' and public.has_permission('gym_settings:manage'));
