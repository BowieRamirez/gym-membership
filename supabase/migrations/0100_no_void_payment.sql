-- Owner fixes 2026-11-19 Q5 (docs/superpowers/plans/2026-11-19-owner-fixes.md): Void payment goes. Customers pay before they get
-- the service, so a mistake is put right with a refund (kept), and payments keep their statuses and audit rows. The function stays
-- for the history it explains (old 'voided' payments, their audit rows), but no one can call it from the app any more.
revoke all on function public.void_payment(bigint, text) from public, anon, authenticated;
