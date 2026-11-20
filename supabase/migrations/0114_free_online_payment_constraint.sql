-- B3 / Q14(a) (2026-11-20): a ₱0 online payment is verified with neither staff verified_by nor xendit_id.
alter table public.payments drop constraint payments_verified_complete;
alter table public.payments add constraint payments_verified_complete check (
  status not in ('verified', 'voided')
  or ((verified_by is not null or xendit_id is not null or amount = 0) and verified_at is not null and receipt_no is not null));
