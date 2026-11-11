-- 0055 (owner, 2026-11-11, before Phase 8): bank transfer is removed from payment_method altogether. The live database had no
-- bank-transfer payment or refund repayment (checked read-only first), so nothing is rewritten. Postgres can't drop one value from an
-- enum, so the type is rebuilt: the two columns move to the new type, the four table rules that name it come back unchanged, and the
-- functions whose arguments or results use it are recreated from their own stored definitions with the same execute grants. 0054's
-- guard is dropped; the type itself now refuses bank transfer.

do $$
declare
	v_old regtype;
	r record;
	g record;
	v_cons text[] := '{}';
	v_funcs text[] := '{}';   -- each entry: new signature | definition | comment
	v_acls text[] := '{}';
	v_sig text;
	i int;
begin
	if exists (select 1 from public.payments where method::text = 'bank_transfer')
	   or exists (select 1 from public.refund_requests where repayment_method::text = 'bank_transfer') then
		raise exception 'bank_transfer rows exist; move them to cash or gcash first';
	end if;

	drop trigger if exists payments_no_bank_transfer on public.payments;
	drop trigger if exists refunds_no_bank_transfer on public.refund_requests;
	drop function if exists public.no_new_bank_transfer();

	alter type public.payment_method rename to payment_method_old;
	v_old := 'public.payment_method_old'::regtype;
	create type public.payment_method as enum ('qr', 'cash', 'gcash');

	-- table rules that name the type: kept as written, dropped while the columns move
	for r in select conrelid::regclass::text as tbl, conname, pg_get_constraintdef(oid) as def from pg_constraint
	         where connamespace = 'public'::regnamespace and contype = 'c' and pg_get_constraintdef(oid) like '%payment_method%' loop
		v_cons := v_cons || format('alter table %s add constraint %I %s', r.tbl, r.conname, replace(r.def, 'payment_method_old', 'payment_method'));
		execute format('alter table %s drop constraint %I', r.tbl, r.conname);
	end loop;

	-- functions whose arguments or results use the type, or whose body names bank transfer (as in p_method in ('gcash','bank_transfer')):
	-- their definition and grants are saved, then they are dropped; bank transfer comes out of those lists
	for r in select p.oid, p.oid::regprocedure::text as sig, pg_get_functiondef(p.oid) as def, p.proacl, obj_description(p.oid, 'pg_proc') as note
	         from pg_proc p where p.pronamespace = 'public'::regnamespace
	           and (v_old = any(p.proargtypes) or p.prorettype = v_old or v_old = any(coalesce(p.proallargtypes, '{}'))
	                or p.prosrc like '%bank_transfer%') loop
		v_sig := 'public.' || replace(regexp_replace(r.sig, '^public\.', ''), 'payment_method_old', 'payment_method');
		v_funcs := v_funcs || array[v_sig,
			regexp_replace(regexp_replace(replace(r.def, 'payment_method_old', 'payment_method'),
				'\s*,\s*''bank_transfer''', '', 'g'), '''bank_transfer''\s*,\s*', '', 'g'),
			coalesce(r.note, '')];
		v_acls := v_acls || coalesce(r.proacl::text, '');
		execute 'drop function ' || r.oid::regprocedure::text;
	end loop;

	alter table public.payments alter column method type public.payment_method using method::text::public.payment_method;
	alter table public.refund_requests alter column repayment_method type public.payment_method using repayment_method::text::public.payment_method;

	foreach v_sig in array v_cons loop execute v_sig; end loop;

	for i in 1 .. coalesce(array_length(v_acls, 1), 0) loop
		v_sig := v_funcs[(i - 1) * 3 + 1];
		execute v_funcs[(i - 1) * 3 + 2];
		if v_funcs[(i - 1) * 3 + 3] <> '' then execute format('comment on function %s is %L', v_sig, v_funcs[(i - 1) * 3 + 3]); end if;
		-- grants exactly as they were: none for public unless it had them, then each saved grantee
		execute format('revoke all on function %s from public', v_sig);
		if v_acls[i] <> '' then
			for g in select a.grantee, a.privilege_type from aclexplode(v_acls[i]::aclitem[]) a
			         where a.grantee <> (select proowner from pg_proc where oid = v_sig::regprocedure) loop
				execute format('grant %s on function %s to %s', g.privilege_type, v_sig,
					case when g.grantee = 0 then 'public' else quote_ident(g.grantee::regrole::text) end);
			end loop;
		end if;
	end loop;

	if exists (select 1 from pg_proc where pronamespace = 'public'::regnamespace and prosrc like '%bank_transfer%') then
		raise exception 'a function still names bank_transfer';
	end if;
	drop type public.payment_method_old;
end $$;

notify pgrst, 'reload schema';
