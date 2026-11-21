-- One-time cleanup of the live test coach_addon record (payment #105 using code LIVETESTCOACH28432 on term 5)
-- so the owner's test member account can test buying the Coach access add-on again.

update public.membership_terms mt
set coach_access_at = null,
    coach_access_payment_id = null
from public.payments p
join public.discounts d on d.discount_id = p.discount_id
where mt.coach_access_payment_id = p.payment_id
  and p.payment_id = 105
  and p.kind::text = 'coach_addon'
  and d.code = 'LIVETESTCOACH28432';
