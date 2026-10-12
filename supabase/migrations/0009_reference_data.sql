-- 0009: reference data the app cannot run without (plan 5.2 and 11).
-- This is a migration, not seed.sql, because production needs these rows too.

insert into public.roles (name, description) values
	('admin',    'The owner: everything'),
	('employee', 'Front desk: payments, check-ins, amenity visits'),
	('coach',    'Trains members and manages their own sessions'),
	('member',   'Gym member. With no active membership they are a guest');

insert into public.permissions (resource, action, description) values
	('payments',        'verify', 'Approve or reject a member''s payment'),
	('payments',        'record', 'Record cash and walk-in payments at the counter'),
	('payments',        'void',   'Void a verified payment'),
	('check_ins',       'record', 'Check members in'),
	('amenity_usages',  'verify', 'Confirm amenity visits'),
	('users',           'manage', 'Deactivate and reactivate users'),
	('roles',           'manage', 'Assign and revoke roles'),
	('catalog',         'manage', 'Packages and amenities'),
	('discounts',       'manage', 'Discount codes'),
	('gym_settings',    'manage', 'Gym name, payment QR, walk-in price'),
	('reports',         'revenue', 'Revenue report'),
	('reports',         'cash',    'Daily cash summary'),
	('audit',           'read',    'Audit log'),
	('sessions',        'manage', 'Manage own training sessions'),
	('shifts',          'manage', 'Post own shifts'),
	('attendance',      'record', 'Record attendance for own sessions');

-- admin
insert into public.role_permissions (role_id, permission_id)
select r.role_id, p.permission_id from public.roles r cross join public.permissions p
where r.name = 'admin' and p.resource not in ('sessions', 'shifts', 'attendance');

-- employee
insert into public.role_permissions (role_id, permission_id)
select r.role_id, p.permission_id from public.roles r join public.permissions p
	on (p.resource, p.action) in (('payments', 'verify'), ('payments', 'record'), ('check_ins', 'record'),
	                              ('amenity_usages', 'verify'), ('reports', 'cash'))
where r.name = 'employee';

-- coach
insert into public.role_permissions (role_id, permission_id)
select r.role_id, p.permission_id from public.roles r join public.permissions p
	on p.resource in ('sessions', 'shifts', 'attendance')
where r.name = 'coach';

insert into public.gym_settings (id, gym_name, qr_account_name, walk_in_price)
values (1, 'Gym Membership', null, 150.00);
