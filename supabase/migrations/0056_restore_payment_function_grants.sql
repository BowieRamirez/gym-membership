-- 0056: 0055 recreated these functions, and Supabase's default privileges gave each one execute for anon, authenticated and
-- service_role on creation. Back to what they had before (as their untouched siblings still show): signed-in users and service_role for
-- the six the app calls, service_role only for the old counter function kept from 0018. anon never had any of them.

revoke execute on function
	public.record_counter_payment(public.payment_kind, public.payment_method, uuid, bigint, text, text, text),
	public.book_amenity(bigint, timestamptz, integer, public.payment_method),
	public.record_membership_refund_paid(bigint, public.payment_method, text),
	public.revenue_lines(date, date, integer, integer),
	public.revenue_summary(date, date),
	public.membership_refund_outflows(date, date)
from public, anon;

revoke execute on function
	public.record_counter_payment_before_terms(public.payment_kind, public.payment_method, uuid, bigint, text, text, text)
from public, anon, authenticated;
