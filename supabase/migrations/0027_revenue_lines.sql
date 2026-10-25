-- Phase 4 (D11): receipt-level lines for the revenue PDF, with the same accounting as revenue_summary (0020):
-- verified payments on their Manila verification day, minus approved/paid refunds on their Manila approval day,
-- voids excluded. Paged (PostgREST caps rows per request), ordered so pages never overlap or skip.
create function public.revenue_lines(p_from date, p_to date, p_offset integer default 0, p_limit integer default 500)
returns table(at timestamptz, receipt text, payer text, item text, kind public.payment_kind, method public.payment_method,
              amount numeric, refund boolean, payment bigint)
language plpgsql stable security definer set search_path='' as $$
begin
  perform public.require_permission('reports:revenue');
  if p_from is null or p_to is null or p_from > p_to or p_offset is null or p_offset < 0 or p_limit is null or p_limit not between 1 and 1000 then
    perform public.fail('bad_input','22023');
  end if;
  return query
  with paid as (
    select p.*, coalesce(pr.full_name, p.walk_in_name, 'Walk-in') as who,
      case p.kind
        when 'membership' then coalesce(snap.name, pkg.name, 'Membership')
        when 'renewal' then 'Extension: ' || coalesce(snap.name, rpkg.name, 'Membership')
        when 'amenity' then coalesce(a.name, 'Amenity')
        else 'Walk-in'
      end as what
    from public.payments p
    left join public.profiles pr on pr.id = p.member_id
    left join public.membership_packages snap on snap.membership_package_id = p.membership_package_snapshot_id
    left join public.user_membership_packages ump on ump.user_membership_package_id = p.user_membership_package_id
    left join public.membership_packages pkg on pkg.membership_package_id = ump.membership_package_id
    left join public.membership_renewals ren on ren.renewal_id = p.renewal_id
    left join public.user_membership_packages rump on rump.user_membership_package_id = ren.user_membership_package_id
    left join public.membership_packages rpkg on rpkg.membership_package_id = rump.membership_package_id
    left join public.user_amenities ua on ua.user_amenity_id = p.user_amenity_id
    left join public.amenities a on a.amenity_id = ua.amenity_id
  )
  select l.at, l.receipt, l.payer, l.item, l.kind, l.method, l.amount, l.refund, l.payment from (
    select p.verified_at as at, p.receipt_no as receipt, p.who as payer, p.what as item, p.kind, p.method, p.amount, false as refund, p.payment_id as payment
      from paid p where p.status = 'verified' and (p.verified_at at time zone 'Asia/Manila')::date between p_from and p_to
    union all
    select r.decided_at, p.receipt_no, p.who, p.what, p.kind, p.method, -r.amount, true, p.payment_id
      from public.refund_requests r join paid p on p.payment_id = r.payment_id
      where r.status in ('approved','paid') and (r.decided_at at time zone 'Asia/Manila')::date between p_from and p_to
  ) l
  order by l.at, l.payment, l.refund
  offset p_offset limit p_limit;
end $$;
revoke all on function public.revenue_lines(date,date,integer,integer) from public,anon,authenticated;
grant execute on function public.revenue_lines(date,date,integer,integer) to authenticated;
