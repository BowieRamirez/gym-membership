-- 0163 (owner, 2026-11-25): Supabase lint 0029 (signed-in users can execute a SECURITY DEFINER function). These six are not
-- called by the app, an Edge Function or a policy; their only callers are other security definer functions (which run as the
-- owner), so closing them to signed-in users changes nothing the app does. The other flagged functions are the app's own API:
-- each checks the caller's permission inside, so they stay callable. avail_amenity and book_slots stay open too: unused by the
-- app, but they check permissions inside and the database tests buy and book through them.
revoke execute on function
  public.can_manage_person(uuid),
  public.is_super_admin(uuid),
  public.daily_cash_summary(date),
  public.membership_refund_outflows(date, date),
  public.packages_codes(),
  public.payroll_deduction_rates_info()
from public, anon, authenticated;
