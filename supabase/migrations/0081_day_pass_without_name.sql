-- Cashier plan Y3 (owner's walk-in document, 2026-11-16): a Day pass needs no account and no name. A blank name is recorded as
-- "Day pass", so payments_payer (a walk-in has a name and no member) still holds. Same change in the cash body and 0080's online copy.
do $$
declare v_def text; r record; fn regprocedure;
begin
  foreach fn in array array[
    'public.record_counter_payment_before_terms(public.payment_kind,public.payment_method,uuid,bigint,text,text,text)'::regprocedure,
    'public.desk_online_payment_body(public.payment_kind,public.payment_method,uuid,bigint,text,text,text)'::regprocedure]
  loop
    v_def := pg_get_functiondef(fn);
    for r in select * from (values
      ($q$if coalesce(btrim(p_walk_in_name), '') = '' then perform public.fail('bad_input', '22023'); end if;$q$, $q$null;   -- 0081: a Day pass may be anonymous$q$),
      ($q$case when p_kind = 'walk_in' then btrim(p_walk_in_name) else null end$q$,
       $q$case when p_kind = 'walk_in' then coalesce(nullif(btrim(p_walk_in_name), ''), 'Day pass') else null end$q$)
    ) as t(old_text, new_text) loop
      if array_length(string_to_array(v_def, r.old_text), 1) <> 2 then raise exception '0081: % lacks "%"', fn, r.old_text; end if;
      v_def := replace(v_def, r.old_text, r.new_text);
    end loop;
    execute v_def;   -- create or replace keeps the owner and grants
  end loop;
end $$;
