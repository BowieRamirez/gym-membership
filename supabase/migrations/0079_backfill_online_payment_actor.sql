-- Cashier plan Y5: online payments confirmed before 0078 were logged as "System" with no role. Name the member who paid.
-- Their method can't be recovered (Xendit's notice didn't carry it), so the details stay as they were.
update public.audit_log a
set actor_id = p.member_id, role = public.main_role(p.member_id), ip_address = null
from public.payments p
where a.action in ('payment.online', 'payment.online_refund') and a.actor_id is null
  and a.entity = 'payments' and p.payment_id::text = a.entity_id and p.member_id is not null;
