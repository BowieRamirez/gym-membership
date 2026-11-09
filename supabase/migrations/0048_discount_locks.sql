-- Phase 1 of the 2026-11-09 plan (owner decisions F5, F7).

-- F7 (changes D19): once a code holds a place (a pending, verified or voided payment), nothing about it can change; it can only be
-- turned off or archived, which the archive guards already govern. Unused codes keep D19: only code and percent are fixed.
-- Installed apps send every column back on save, so unchanged values must still pass.
alter table public.discounts add column show_on_packages boolean not null default false;

create or replace function public.guard_discount_fixed() returns trigger
language plpgsql security definer set search_path='' as $$
begin
  if upper(new.code) is distinct from upper(old.code) or new.percent is distinct from old.percent then
    raise exception 'discount_fixed' using errcode='P0002';
  end if;
  if (new.starts_at, new.expires_at, new.max_uses, new.show_on_packages) is distinct from (old.starts_at, old.expires_at, old.max_uses, old.show_on_packages)
     and public.discount_held(old.discount_id) > 0 then
    raise exception 'discount_locked' using errcode='P0002';
  end if;
  return new;
end $$;
revoke all on function public.guard_discount_fixed() from public, anon, authenticated;
drop trigger discounts_fixed_guard on public.discounts;
create trigger discounts_fixed_guard before update of code, percent, starts_at, expires_at, max_uses, show_on_packages on public.discounts
  for each row execute function public.guard_discount_fixed();

-- F5: the codes a signed-in member could still use, for the Packages page. Members never read the table itself (0006).
create function public.packages_codes() returns table(code text, percent int, expires_at timestamptz)
language plpgsql stable security definer set search_path='' as $$
begin
  perform public.require_active_user();
  return query select d.code, d.percent, d.expires_at from public.discounts d
    where d.show_on_packages and d.is_active and d.starts_at <= now() and d.expires_at > now()
      and (d.max_uses is null or public.discount_held(d.discount_id) < d.max_uses)
      and not exists (select 1 from public.payments p where p.member_id = auth.uid() and p.discount_id = d.discount_id
                      and p.status in ('pending', 'verified', 'voided'))
    order by d.expires_at, d.code;
end $$;
revoke all on function public.packages_codes() from public, anon, authenticated;
grant execute on function public.packages_codes() to authenticated;
