-- Read-only checks to run against the REAL database before pushing 0018 (paste into the Supabase SQL editor).
-- Rows 1-4 must all be 0, otherwise 0018 will stop with ambiguous_membership_history / overlapping_membership_history
-- (it rolls back, but then you need to look at those memberships by hand). Row 5 is information only.

select 'multiple verified membership payments on one membership (must be 0)' as check, count(*) as n from (
  select 1 from public.payments where kind = 'membership' and status = 'verified'
  group by user_membership_package_id having count(*) > 1) x
union all
select 'cancelled membership that still has a verified payment (must be 0)', count(*)
from public.user_membership_packages u where u.status = 'cancelled' and exists (
  select 1 from public.payments p left join public.membership_renewals r on r.renewal_id = p.renewal_id
  where p.status = 'verified' and (p.user_membership_package_id = u.user_membership_package_id
    or r.user_membership_package_id = u.user_membership_package_id))
union all
select 'completed renewal without a verified payment (must be 0)', count(*)
from public.membership_renewals r join public.user_membership_packages u using (user_membership_package_id)
where r.status = 'completed' and u.status in ('active', 'expired')
  and not exists (select 1 from public.payments p where p.renewal_id = r.renewal_id and p.status = 'verified')
union all
select 'membership whose renewals do not add up to its end date (must be 0)', count(*)
from public.user_membership_packages u where u.status in ('active', 'expired') and u.ends_at is not null
  and exists (select 1 from public.membership_renewals r where r.user_membership_package_id = u.user_membership_package_id and r.status = 'completed')
  and u.ends_at <> (select max(r.new_ends_at) from public.membership_renewals r
    where r.user_membership_package_id = u.user_membership_package_id and r.status = 'completed')
union all
select 'INFO: active/expired membership with no verified payment (becomes a free legacy term that cannot be refunded)', count(*)
from public.user_membership_packages u where u.status in ('active', 'expired') and u.starts_at is not null and u.ends_at is not null
  and not exists (select 1 from public.payments p where p.user_membership_package_id = u.user_membership_package_id
    and p.kind = 'membership' and p.status = 'verified');

-- Rolled-back dry run against the live data (this does not change anything): paste the WHOLE of
-- supabase/migrations/0018_membership_terms_and_refunds.sql in place of the marker, run it, and expect "DRY RUN OK".
--
--   begin;
--   <contents of 0018_membership_terms_and_refunds.sql>
--   select 'DRY RUN OK' as result;
--   rollback;
--
-- The command-line equivalent that only lists what would be applied is: npx supabase db push --dry-run
