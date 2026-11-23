-- Allow one regular Day pass and one Coach-included Day pass to be on sale at the same time.
-- Keep anonymous walk-in price lookup deterministic when both Day pass packages are published.

drop index if exists public.membership_packages_one_day_pass;
create unique index membership_packages_one_day_pass
  on public.membership_packages (is_day_pass, includes_coach)
  where is_day_pass and is_active and published_at is not null;

do $$
declare v_def text; r record;
begin
  for r in select * from (values
    ('public.record_counter_payment_before_terms(public.payment_kind,public.payment_method,uuid,bigint,text,text,text)',
     $q$select coalesce((select price from public.membership_packages where is_day_pass and is_active and published_at is not null), walk_in_price) into v_price from public.gym_settings where id = 1;$q$,
     $q$select coalesce(
       (select price from public.membership_packages where membership_package_id = p_item_id and is_day_pass and is_active and published_at is not null),
       (select price from public.membership_packages where is_day_pass and not includes_coach and is_active and published_at is not null order by membership_package_id desc limit 1),
       (select price from public.membership_packages where is_day_pass and is_active and published_at is not null order by membership_package_id desc limit 1),
       walk_in_price) into v_price from public.gym_settings where id = 1;$q$),
    ('public.desk_online_payment_body(public.payment_kind,public.payment_method,uuid,bigint,text,text,text)',
     $q$select coalesce((select price from public.membership_packages where is_day_pass and is_active and published_at is not null), walk_in_price) into v_price from public.gym_settings where id = 1;$q$,
     $q$select coalesce(
       (select price from public.membership_packages where membership_package_id = p_item_id and is_day_pass and is_active and published_at is not null),
       (select price from public.membership_packages where is_day_pass and not includes_coach and is_active and published_at is not null order by membership_package_id desc limit 1),
       (select price from public.membership_packages where is_day_pass and is_active and published_at is not null order by membership_package_id desc limit 1),
       walk_in_price) into v_price from public.gym_settings where id = 1;$q$)
  ) as t(fn, old_text, new_text) loop
    v_def := pg_get_functiondef(r.fn::regprocedure);
    if array_length(string_to_array(v_def, r.old_text), 1) <> 2 then
      raise exception '0125: % lacks expected walk_in price lookup', r.fn;
    end if;
    execute replace(v_def, r.old_text, r.new_text);
  end loop;
end $$;

revoke all on function public.record_counter_payment_before_terms(public.payment_kind, public.payment_method, uuid, bigint, text, text, text),
  public.desk_online_payment_body(public.payment_kind, public.payment_method, uuid, bigint, text, text, text) from public, anon, authenticated;
