-- Phase 10, D4 (owner, 2026-11-06): a payment of 0 is refused everywhere (cashier, member purchase, direct calls) unless a discount
-- brought the price to 0. Every way to pay inserts into payments, so one trigger covers them all. Insert only: payments recorded
-- before this keep working. Named to run before payments_active_account and snapshot_membership_payment (triggers fire by name).

create function public.refuse_free_payment() returns trigger
language plpgsql
set search_path = ''
as $$
begin
	if new.amount <= 0 and new.discount_amount <= 0 then
		perform public.fail('zero_amount', '22023');
	end if;
	return new;
end;
$$;

create trigger payments_a_not_free before insert on public.payments
	for each row execute function public.refuse_free_payment();

revoke all on function public.refuse_free_payment() from public, anon, authenticated;
