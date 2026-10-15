-- 0014: live amenity status, saved amenities and their alerts (role redesign, phase 3)
--
-- An amenity is now available, in use or unavailable. Only set_amenity_status can change that: it checks the permission,
-- writes the audit log and alerts the members who saved the amenity. The old purchase tables and functions are not touched.

create type public.amenity_status as enum ('available', 'in_use', 'unavailable');

-- the alert type; it is not used anywhere in this file outside a function body, so adding it inside this transaction is fine
alter type public.notification_type add value if not exists 'amenity';

alter table public.amenities
	add column status            public.amenity_status not null default 'available',
	add column status_reason     text,
	add column back_at           timestamptz,
	add column status_changed_by uuid references public.profiles (id),
	add column status_changed_at timestamptz,
	add constraint amenities_status_detail check (
		(status = 'unavailable' or (status_reason is null and back_at is null))
		and (status_reason is null or char_length(status_reason) <= 200)
	);

comment on column public.amenities.status is 'Live status. Written only by set_amenity_status';
comment on column public.amenities.status_reason is 'Why it is unavailable (maintenance, cleaning, closed ...); only while unavailable';
comment on column public.amenities.back_at is 'When it is expected back; only while unavailable';

-- the status columns are no longer writable from the app: catalog edits keep the columns they always had
revoke insert, update on public.amenities from authenticated;
grant insert (name, description, price, duration_days, is_active, image_path) on public.amenities to authenticated;
grant update (name, description, price, duration_days, is_active, image_path) on public.amenities to authenticated;

-- members who saved an amenity to hear when it changes
create table public.amenity_follows (
	member_id  uuid        not null references public.members (user_id) on delete cascade,
	amenity_id bigint      not null references public.amenities (amenity_id) on delete cascade,
	created_at timestamptz not null default now(),
	primary key (member_id, amenity_id)
);

create index amenity_follows_amenity_id_idx on public.amenity_follows (amenity_id);

alter table public.amenity_follows enable row level security;
revoke all on public.amenity_follows from anon, authenticated;
grant select on public.amenity_follows to authenticated;
create policy amenity_follows_select on public.amenity_follows for select to authenticated
	using (public.is_active_user() and member_id = auth.uid());

-- save or un-save an amenity (members only; saving twice is fine)
create function public.set_amenity_followed(p_amenity_id bigint, p_follow boolean) returns void
language plpgsql security definer
set search_path = ''
as $$
begin
	perform public.require_active_user();
	if not exists (select 1 from public.members where user_id = auth.uid()) then perform public.fail('forbidden', '42501'); end if;

	if p_follow then
		if not exists (select 1 from public.amenities where amenity_id = p_amenity_id and is_active) then perform public.fail('not_found', '22023'); end if;
		insert into public.amenity_follows (member_id, amenity_id) values (auth.uid(), p_amenity_id) on conflict do nothing;
	else
		delete from public.amenity_follows where member_id = auth.uid() and amenity_id = p_amenity_id;
	end if;
end;
$$;

-- Change an amenity's status. Returns the members who were alerted, so the screen can show who was told.
create function public.set_amenity_status(p_amenity_id bigint, p_status public.amenity_status, p_reason text default null, p_back_at timestamptz default null)
returns table (member_id uuid, full_name text)
language plpgsql security definer
set search_path = ''
as $$
#variable_conflict use_column
declare
	v public.amenities;
	v_reason text := nullif(btrim(p_reason), '');
	v_title text;
	v_body text;
begin
	perform public.require_permission('amenities:set_status');

	select * into v from public.amenities where amenity_id = p_amenity_id for update;
	if not found then perform public.fail('not_found', '22023'); end if;

	if p_status <> 'unavailable' then v_reason := null; p_back_at := null; end if;   -- these only belong to "unavailable"
	if char_length(coalesce(v_reason, '')) > 200 then perform public.fail('bad_input', '22023'); end if;
	if p_back_at is not null and p_back_at <= now() then perform public.fail('bad_input', '22023'); end if;

	-- nothing changed: change nothing, tell nobody
	if v.status = p_status and v.status_reason is not distinct from v_reason and v.back_at is not distinct from p_back_at then return; end if;

	update public.amenities
	set status = p_status, status_reason = v_reason, back_at = p_back_at, status_changed_by = auth.uid(), status_changed_at = now()
	where amenity_id = p_amenity_id;

	perform public.audit('amenity.status', 'amenities', p_amenity_id::text,
		jsonb_build_object('from', v.status, 'to', p_status, 'reason', v_reason, 'back_at', p_back_at));

	if p_status = 'in_use' then return; end if;   -- "in use" changes all day: not worth an alert

	if p_status = 'available' then
		v_title := v.name || ' is available';
		v_body := null;
	else
		v_title := v.name || ' is unavailable';
		v_body := concat_ws('. ', v_reason,
			case when p_back_at is not null then 'Back about ' || to_char(p_back_at at time zone 'Asia/Manila', 'Dy DD Mon, HH12:MI AM') end);
	end if;

	return query
		with told as (
			insert into public.user_notifications (user_id, type, title, body, reference)
			select f.member_id, 'amenity', v_title, v_body, jsonb_build_object('amenity_id', p_amenity_id)
			from public.amenity_follows f
			join public.profiles p on p.id = f.member_id and p.is_active
			where f.amenity_id = p_amenity_id
			returning user_id
		)
		select t.user_id, pr.full_name from told t join public.profiles pr on pr.id = t.user_id order by pr.full_name;
end;
$$;

-- who may set a status: admin and front desk
insert into public.permissions (resource, action, description) values
	('amenities', 'set_status', 'Mark an amenity available, in use or unavailable');

insert into public.role_permissions (role_id, permission_id)
select r.role_id, p.permission_id from public.roles r join public.permissions p on p.resource = 'amenities' and p.action = 'set_status'
where r.name in ('admin', 'employee');

revoke all on function public.set_amenity_followed(bigint, boolean) from public, anon, authenticated;
revoke all on function public.set_amenity_status(bigint, public.amenity_status, text, timestamptz) from public, anon, authenticated;
grant execute on function public.set_amenity_followed(bigint, boolean) to authenticated;
grant execute on function public.set_amenity_status(bigint, public.amenity_status, text, timestamptz) to authenticated;
