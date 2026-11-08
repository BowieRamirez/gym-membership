-- D17 and D28: the role descriptions use the names people see in the app, so the dashboard matches the screens.
-- Only the description changes. The name column (admin, subadmin, employee) is the code every permission check reads; it stays.

update public.roles set description = 'System admin (the owner): everything' where name = 'admin';
update public.roles set description = 'Admin: front desk plus catalog, discounts, revenue and member/coach accounts' where name = 'subadmin';
