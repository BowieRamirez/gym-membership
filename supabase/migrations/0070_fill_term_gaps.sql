-- G26 (owner, 2026-11-15): a paid membership term starts on the first moment no other live term covers, so it fills a gap
-- (for example one a refund left before a term bought ahead) instead of queueing after the last term. Terms that start
-- after it and that it runs into all move back by the overlap, keeping their lengths and the gaps between them.
-- Only activation changes: the estimate create_membership_extension stores is replaced here when the payment is verified.

create function public.next_term_start(p_member uuid, p_at timestamptz) returns timestamptz
language plpgsql stable security definer set search_path='' as $$
declare v_start timestamptz := p_at; v_end timestamptz;
begin
  loop   -- walk an unbroken run of live terms to its end
    select max(ends_at) into v_end from public.membership_terms
      where member_id=p_member and cancelled_at is null and starts_at<=v_start and ends_at>v_start;
    exit when v_end is null;
    v_start := v_end;
  end loop;
  return v_start;
end $$;
revoke all on function public.next_term_start(uuid,timestamptz) from public, anon, authenticated;

create or replace function public.activate_payment_target(p_payment_id bigint) returns void
language plpgsql security definer set search_path='' as $$
declare v_pay public.payments; v_ump bigint; v_start timestamptz; v_end timestamptz; v_next timestamptz;
begin
  select * into v_pay from public.payments where payment_id=p_payment_id;
  if not found or v_pay.status<>'verified' then perform public.fail('already_processed','P0002'); end if;
  if v_pay.kind in ('membership','renewal') then
    perform 1 from public.members where user_id=v_pay.member_id for update;
    if exists(select 1 from public.membership_terms where payment_id=p_payment_id) then
      perform public.fail('already_processed','P0002');
    end if;
    v_ump:=v_pay.user_membership_package_id;
    if v_pay.renewal_id is not null then
      select user_membership_package_id into v_ump from public.membership_renewals where renewal_id=v_pay.renewal_id;
    end if;
    v_start:=public.next_term_start(v_pay.member_id, v_pay.verified_at);
    v_end:=v_start + make_interval(days=>v_pay.membership_duration_days);
    select min(starts_at) into v_next from public.membership_terms
      where member_id=v_pay.member_id and cancelled_at is null and starts_at>=v_start;
    if v_next < v_end then
      update public.membership_terms set starts_at=starts_at+(v_end-v_next), ends_at=ends_at+(v_end-v_next)
        where member_id=v_pay.member_id and cancelled_at is null and starts_at>=v_start;
    end if;
    insert into public.membership_terms(member_id,user_membership_package_id,payment_id,membership_package_id,
      package_name,duration_days,amount_paid,starts_at,ends_at,snapshot_source)
    values(v_pay.member_id,v_ump,p_payment_id,v_pay.membership_package_snapshot_id,
      v_pay.membership_package_name,v_pay.membership_duration_days,v_pay.amount,v_start,v_end,v_pay.membership_snapshot_source);
    if v_pay.renewal_id is not null then
      update public.membership_renewals set status='completed',completed_at=v_pay.verified_at,
        previous_ends_at=v_start,new_starts_at=v_start,new_ends_at=v_end where renewal_id=v_pay.renewal_id;
    end if;
    perform public.project_membership_terms(v_pay.member_id);
  elsif v_pay.user_amenity_id is not null then
    update public.user_amenities ua set status='active',
      expires_at=case when a.duration_days is null then null else now()+make_interval(days=>a.duration_days) end
    from public.amenities a where ua.user_amenity_id=v_pay.user_amenity_id and a.amenity_id=ua.amenity_id;
  end if;
end $$;
