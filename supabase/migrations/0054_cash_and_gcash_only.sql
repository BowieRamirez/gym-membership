-- 0054 (owner, 2026-11-11, before Phase 8): the desk takes Cash or GCash only. Any new payment, or a refund repaid, by bank transfer is
-- refused (bad_input), wherever it comes from. Rows already recorded as bank transfer stay as history: the guard looks only at a row
-- that becomes bank transfer, so voiding or otherwise updating an old one still works. The enum value stays for those rows.

-- the column to watch comes as the trigger's argument (payments.method, refund_requests.repayment_method)
create function public.no_new_bank_transfer() returns trigger
language plpgsql set search_path = '' as $$
begin
	if to_jsonb(new) ->> tg_argv[0] = 'bank_transfer'
	   and (tg_op = 'INSERT' or (to_jsonb(old) ->> tg_argv[0]) is distinct from 'bank_transfer') then
		perform public.fail('bad_input', '22023');
	end if;
	return new;
end $$;

create trigger payments_no_bank_transfer before insert or update of method on public.payments
	for each row execute function public.no_new_bank_transfer('method');
create trigger refunds_no_bank_transfer before insert or update of repayment_method on public.refund_requests
	for each row execute function public.no_new_bank_transfer('repayment_method');
