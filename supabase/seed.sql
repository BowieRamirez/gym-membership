-- Demo catalog for local development (`supabase db reset`) or a dev project (`supabase db push --include-seed`).
-- Roles, permissions and gym_settings are in migration 0009 because production needs them too.
-- Demo people are created by supabase/scripts/seed-demo-users.mjs, which goes through Auth so the sign-up trigger runs.

insert into public.membership_packages (name, description, price, duration_days) values
	('Monthly',   'Full gym floor access for 30 days.',              1500.00,   30),
	('Quarterly', '90 days of access at a lower monthly rate.',      4000.00,   90),
	('Annual',    'A full year, best value for regulars.',          14000.00,  365);

insert into public.amenities (name, description, price, duration_days, is_active) values
	('Pool',          '25 m lap pool, open 6:00 to 21:00.',                    300.00, 30, true),
	('Sauna',         'Dry sauna next to the locker rooms.',                   200.00, 30, true),
	('Group classes', 'Spin, yoga and circuit classes on the studio floor.',   500.00, 30, true),
	('Boxing ring',   'Closed for maintenance.',                               400.00, 30, false);

insert into public.discounts (code, percent, starts_at, expires_at) values
	('WELCOME10', 10, now() - interval '1 day', now() + interval '60 days');

update public.gym_settings set gym_name = 'Gym Membership (demo)', qr_account_name = 'Demo Gym', walk_in_price = 150.00 where id = 1;
