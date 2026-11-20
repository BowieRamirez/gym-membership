-- Owner fixes 2026-11-19, final review: is_day_pass (0098) must travel with a new version of the package, and like the price it
-- can't change once a package is on sale (a published Monthly flipped to a Day pass would make its pending payments end at midnight).
do $$
declare v_def text; r record;
begin
  for r in select * from (values
    ('public.new_package_version(bigint)',
     $q$insert into public.membership_packages (name, description, price, duration_days, benefits, image_path, replaces_id, published_at)$q$,
     $q$insert into public.membership_packages (name, description, price, duration_days, benefits, image_path, replaces_id, published_at, is_day_pass)$q$),
    ('public.new_package_version(bigint)',
     $q$values (v.name, v.description, v.price, v.duration_days, v.benefits, v.image_path, p_id, null)$q$,
     $q$values (v.name, v.description, v.price, v.duration_days, v.benefits, v.image_path, p_id, null, v.is_day_pass)$q$),
    ('public.guard_catalog_lifecycle()',
     $q$(new.name, new.description, new.price, new.duration_days, new.benefits, new.image_path, new.published_at, new.replaces_id)$q$,
     $q$(new.name, new.description, new.price, new.duration_days, new.benefits, new.image_path, new.published_at, new.replaces_id, new.is_day_pass)$q$),
    ('public.guard_catalog_lifecycle()',
     $q$(old.name, old.description, old.price, old.duration_days, old.benefits, old.image_path, old.published_at, old.replaces_id)$q$,
     $q$(old.name, old.description, old.price, old.duration_days, old.benefits, old.image_path, old.published_at, old.replaces_id, old.is_day_pass)$q$)
  ) as t(fn, old_text, new_text) loop
    v_def := pg_get_functiondef(r.fn::regprocedure);
    if array_length(string_to_array(v_def, r.old_text), 1) <> 2 then raise exception '0105: % lacks "%"', r.fn, r.old_text; end if;
    execute replace(v_def, r.old_text, r.new_text);   -- create or replace keeps the owner and grants
  end loop;
end $$;
