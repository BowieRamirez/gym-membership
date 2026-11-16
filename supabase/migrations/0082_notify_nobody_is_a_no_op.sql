-- Cashier plan, final review (Critical): a Day pass paid online at the desk (0080) has no member. Notifying "nobody" inserted a
-- null user_id and failed, so expire_payments rolled back on every run once such a payment lapsed (nothing expired any more, and
-- book_amenity, which runs expire_payments first, failed for everyone); rejecting one ("Customer left") failed the same way.
-- Fixed once where every caller goes through: a notification for no person is skipped.
create or replace function public.notify(p_user uuid, p_type public.notification_type, p_title text, p_body text default null, p_reference jsonb default null)
returns void
language sql security definer
set search_path = ''
as $$
	insert into public.user_notifications (user_id, type, title, body, reference)
	select p_user, p_type, p_title, p_body, p_reference where p_user is not null;
$$;
revoke all on function public.notify(uuid, public.notification_type, text, text, jsonb) from public, anon, authenticated;
