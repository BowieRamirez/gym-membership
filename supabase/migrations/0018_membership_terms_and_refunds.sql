-- Phase 7: paid windows, queued extensions and administrator-controlled refunds.
-- Prepare locally first. Review the historical backfill before applying to production.
-- Run supabase/preflight/0018_preflight.sql and a rolled-back dry run first. This file is not safe to re-run:
-- if it stops part-way, restore from backup instead of running it again.

alter table public.payments
  add column membership_package_snapshot_id bigint references public.membership_packages,
  add column membership_package_name text,
  add column membership_duration_days int check (membership_duration_days > 0),
  add column membership_snapshot_source text check (membership_snapshot_source in ('purchase','legacy_catalog'));

create table public.membership_terms (
  term_id bigint generated always as identity primary key,
  member_id uuid not null references public.members(user_id),
  user_membership_package_id bigint not null references public.user_membership_packages,
  payment_id bigint unique references public.payments,
  membership_package_id bigint not null references public.membership_packages,
  package_name text not null,
  duration_days int not null check (duration_days > 0),
  amount_paid numeric(12,2) not null check (amount_paid >= 0),
  starts_at timestamptz not null,
  ends_at timestamptz not null check (ends_at > starts_at),
  snapshot_source text not null check (snapshot_source in ('purchase', 'legacy_catalog')),
  cancelled_at timestamptz,
  cancelled_by uuid references public.profiles,
  cancellation_reason text,
  created_at timestamptz not null default now()
);
create index membership_terms_member_dates on public.membership_terms(member_id, ends_at, starts_at) where cancelled_at is null;

create table public.refund_requests (
  refund_request_id bigint generated always as identity primary key,
  term_id bigint not null unique references public.membership_terms,
  payment_id bigint not null unique references public.payments,
  member_id uuid not null references public.members(user_id),
  amount numeric(12,2) not null check (amount >= 0),
  reason text not null check (length(btrim(reason)) between 1 and 1000),
  requested_at timestamptz not null default now(),
  status text not null default 'pending' check (status in ('pending','rejected','approved','paid')),
  decided_by uuid references public.profiles,
  decided_at timestamptz,
  decision_reason text,
  paid_by uuid references public.profiles,
  paid_at timestamptz,
  repayment_method public.payment_method,
  repayment_reference text,
  check ((status = 'pending' and decided_by is null and decided_at is null)
      or (status <> 'pending' and decided_by is not null and decided_at is not null)),
  check ((status = 'paid' and paid_by is not null and paid_at is not null and repayment_method is not null
           and repayment_method <> 'qr'
           and (repayment_method = 'cash' or coalesce(length(btrim(repayment_reference)),0) > 0))
      or (status <> 'paid' and paid_by is null and paid_at is null and repayment_method is null and repayment_reference is null))
);
create index refund_requests_queue on public.refund_requests(status, requested_at);

-- Recover only history the previous schema actually retained. Do not invent old catalog prices/names.
do $$ begin
  if exists (select 1 from public.payments where kind='membership' and status='verified'
      group by user_membership_package_id having count(*) > 1)
    or exists(select 1 from public.user_membership_packages u where u.status='cancelled' and exists(
      select 1 from public.payments p left join public.membership_renewals r on r.renewal_id=p.renewal_id
      where p.status='verified' and (p.user_membership_package_id=u.user_membership_package_id
        or r.user_membership_package_id=u.user_membership_package_id))) then
    perform public.fail('ambiguous_membership_history', '22023');
  end if;
end $$;

update public.payments p set membership_package_snapshot_id=k.membership_package_id,
  membership_package_name=k.name, membership_duration_days=k.duration_days, membership_snapshot_source='legacy_catalog'
from public.user_membership_packages u join public.membership_packages k using(membership_package_id)
where p.kind='membership' and p.user_membership_package_id=u.user_membership_package_id;
update public.payments p set membership_package_snapshot_id=k.membership_package_id,
  membership_package_name=k.name, membership_duration_days=k.duration_days, membership_snapshot_source='legacy_catalog'
from public.membership_renewals r join public.user_membership_packages u using(user_membership_package_id)
join public.membership_packages k using(membership_package_id)
where p.kind='renewal' and p.renewal_id=r.renewal_id;

insert into public.membership_terms(member_id,user_membership_package_id,payment_id,membership_package_id,
  package_name,duration_days,amount_paid,starts_at,ends_at,snapshot_source)
select u.member_id,u.user_membership_package_id,p.payment_id,k.membership_package_id,k.name,k.duration_days,
  coalesce(p.amount,0),u.starts_at,
  coalesce((select r.previous_ends_at from public.membership_renewals r
    where r.user_membership_package_id=u.user_membership_package_id and r.status='completed'
    order by r.new_starts_at,r.renewal_id limit 1),u.ends_at), 'legacy_catalog'
from public.user_membership_packages u join public.membership_packages k using(membership_package_id)
left join public.payments p on p.user_membership_package_id=u.user_membership_package_id and p.status='verified'
where u.status in ('active','expired') and u.starts_at is not null and u.ends_at is not null;

insert into public.membership_terms(member_id,user_membership_package_id,payment_id,membership_package_id,
  package_name,duration_days,amount_paid,starts_at,ends_at,snapshot_source)
select u.member_id,u.user_membership_package_id,p.payment_id,k.membership_package_id,k.name,k.duration_days,
  p.amount,r.new_starts_at,r.new_ends_at,'legacy_catalog'
from public.membership_renewals r join public.user_membership_packages u using(user_membership_package_id)
join public.membership_packages k using(membership_package_id)
join public.payments p on p.renewal_id=r.renewal_id and p.status='verified'
where r.status='completed' and u.status in ('active','expired');

-- The converted terms must add up to what the old row says. Anything else (an earlier renewal voided while a later one stayed
-- completed, a completed renewal with no verified payment) would hand out or drop days, so stop and let a person decide.
do $$ begin
  if exists (select 1 from public.user_membership_packages u
      where u.status in ('active','expired') and u.ends_at is not null
        and exists (select 1 from public.membership_terms t where t.user_membership_package_id=u.user_membership_package_id)
        and (select max(t.ends_at) from public.membership_terms t where t.user_membership_package_id=u.user_membership_package_id) <> u.ends_at)
    or exists (select 1 from public.membership_renewals r join public.user_membership_packages u using(user_membership_package_id)
      where r.status='completed' and u.status in ('active','expired')
        and not exists (select 1 from public.membership_terms t join public.payments p on p.payment_id=t.payment_id
          where p.renewal_id=r.renewal_id)) then
    perform public.fail('ambiguous_membership_history', '22023');
  end if;
end $$;

do $$ begin
  if exists (select 1 from public.membership_terms a join public.membership_terms b
    on a.member_id=b.member_id and a.term_id<b.term_id
    and a.starts_at<b.ends_at and b.starts_at<a.ends_at) then
    perform public.fail('overlapping_membership_history', '22023');
  end if;
end $$;

create function public.snapshot_membership_payment() returns trigger
language plpgsql security definer set search_path='' as $$
declare v_package bigint; v_pkg public.membership_packages;
begin
  if new.kind='membership' then
    select membership_package_id into v_package from public.user_membership_packages
      where user_membership_package_id=new.user_membership_package_id;
  elsif new.kind='renewal' then
    select u.membership_package_id into v_package from public.membership_renewals r
      join public.user_membership_packages u using(user_membership_package_id) where r.renewal_id=new.renewal_id;
  else return new;
  end if;
  select * into v_pkg from public.membership_packages
    where membership_package_id=coalesce(new.membership_package_snapshot_id,v_package);
  if not found then perform public.fail('not_found','22023'); end if;
  new.membership_package_snapshot_id := v_pkg.membership_package_id;
  new.membership_package_name := v_pkg.name;
  new.membership_duration_days := v_pkg.duration_days;
  new.membership_snapshot_source := 'purchase';
  return new;
end $$;
create trigger snapshot_membership_payment before insert on public.payments
for each row execute function public.snapshot_membership_payment();

create or replace function public.is_active_member(p_user uuid default auth.uid()) returns boolean
language sql stable security definer set search_path='' as $$
  select exists(select 1 from public.membership_terms t join public.profiles p on p.id=t.member_id
    where t.member_id=p_user and p.is_active and t.cancelled_at is null and t.starts_at<=now() and t.ends_at>now());
$$;

-- Compatibility only. Access always reads the exact term windows, including gaps.
create function public.project_membership_terms(p_member uuid) returns void
language plpgsql security definer set search_path='' as $$
begin
  update public.user_membership_packages set status='expired' where member_id=p_member and status='active';
  update public.user_membership_packages u set
    starts_at=coalesce((select min(t.starts_at) from public.membership_terms t
      where t.user_membership_package_id=u.user_membership_package_id and t.cancelled_at is null),u.starts_at),
    ends_at=coalesce((select max(t.ends_at) from public.membership_terms t
      where t.user_membership_package_id=u.user_membership_package_id and t.cancelled_at is null),u.ends_at),
    status=case
      when exists(select 1 from public.membership_terms t where t.user_membership_package_id=u.user_membership_package_id
        and t.cancelled_at is null and t.starts_at<=now() and t.ends_at>now()) then 'active'::public.membership_status
      when exists(select 1 from public.membership_terms t where t.user_membership_package_id=u.user_membership_package_id
        and t.cancelled_at is null) then 'expired'::public.membership_status
      else 'cancelled'::public.membership_status end
  where u.member_id=p_member and exists(select 1 from public.membership_terms t
    where t.user_membership_package_id=u.user_membership_package_id);
end $$;

create or replace function public.activate_payment_target(p_payment_id bigint) returns void
language plpgsql security definer set search_path='' as $$
declare v_pay public.payments; v_ump bigint; v_start timestamptz; v_end timestamptz;
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
    select greatest(coalesce(max(ends_at),v_pay.verified_at),v_pay.verified_at) into v_start
      from public.membership_terms where member_id=v_pay.member_id and cancelled_at is null;
    v_end:=v_start + make_interval(days=>v_pay.membership_duration_days);
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

create function public.create_membership_extension(p_member uuid,p_package_id bigint,p_discount_code text) returns public.payments
language plpgsql security definer set search_path='' as $$
declare v_pkg public.membership_packages; v_ump bigint; v_ren bigint; v_start timestamptz;
  v_disc record; v_pay public.payments; v_kind public.payment_kind;
begin
  perform 1 from public.members m join public.profiles p on p.id=m.user_id
    where m.user_id=p_member and p.is_active for update of m;
  if not found then perform public.fail('not_found','22023'); end if;
  if exists(select 1 from public.payments where member_id=p_member and status='pending' and kind in ('membership','renewal')) then
    perform public.fail('renewal_pending','P0002');
  end if;
  select * into v_pkg from public.membership_packages where membership_package_id=p_package_id and is_active;
  if not found then perform public.fail('not_found','22023'); end if;
  select * into v_disc from public.apply_discount(p_member,p_discount_code,v_pkg.price);
  select user_membership_package_id into v_ump from public.user_membership_packages
    where member_id=p_member and starts_at is not null order by created_at desc,user_membership_package_id desc limit 1;
  if v_ump is null then
    insert into public.user_membership_packages(member_id,membership_package_id) values(p_member,p_package_id)
      returning user_membership_package_id into v_ump;
    v_kind:='membership';
  else
    select greatest(coalesce(max(ends_at),now()),now()) into v_start from public.membership_terms
      where member_id=p_member and cancelled_at is null;
    insert into public.membership_renewals(user_membership_package_id,previous_ends_at,new_starts_at,new_ends_at)
      values(v_ump,v_start,v_start,v_start+make_interval(days=>v_pkg.duration_days)) returning renewal_id into v_ren;
    v_kind:='renewal';
  end if;
  insert into public.payments(payment_code,kind,method,amount,discount_id,discount_amount,member_id,
      user_membership_package_id,renewal_id,expires_at,membership_package_snapshot_id)
    select public.generate_payment_code(),v_kind,'qr',v_disc.amount,v_disc.discount_id,v_disc.discount_amount,p_member,
      case when v_ren is null then v_ump else null end,v_ren,now()+make_interval(hours=>payment_expiry_hours),p_package_id
    from public.gym_settings where id=1 returning * into v_pay;
  return v_pay;
end $$;

create function public.extend_membership(p_package_id bigint,p_discount_code text default null) returns public.payments
language plpgsql security definer set search_path='' as $$
begin
  perform public.require_active_user();
  return public.create_membership_extension(auth.uid(),p_package_id,p_discount_code);
end $$;

-- Older clients still use these entry points. They must not bypass the member lock or snapshot rules.
create or replace function public.avail_membership(p_package_id bigint,p_discount_code text default null) returns public.payments
language plpgsql security definer set search_path='' as $$
begin
  perform public.require_active_user();
  perform 1 from public.members where user_id=auth.uid() for update;
  if exists(select 1 from public.user_membership_packages where member_id=auth.uid() and status='pending')
    or exists(select 1 from public.membership_terms where member_id=auth.uid() and cancelled_at is null and ends_at>now()) then
    perform public.fail('already_availed','P0002');
  end if;
  return public.create_membership_extension(auth.uid(),p_package_id,p_discount_code);
end $$;

create or replace function public.renew_membership(p_ump_id bigint,p_discount_code text default null) returns public.payments
language plpgsql security definer set search_path='' as $$
declare v_package bigint;
begin
  perform public.require_active_user();
  select membership_package_id into v_package from public.user_membership_packages
    where user_membership_package_id=p_ump_id and member_id=auth.uid() and status in ('active','expired');
  if not found then perform public.fail('not_renewable','22023'); end if;
  return public.create_membership_extension(auth.uid(),v_package,p_discount_code);
end $$;

alter function public.verify_payment(bigint,boolean,text) rename to verify_payment_before_terms;
create function public.verify_payment(p_payment_id bigint,p_approve boolean,p_reason text default null) returns void
language plpgsql security definer set search_path='' as $$
declare v_pay public.payments;
begin
  perform public.require_permission('payments:verify');
  select * into v_pay from public.payments where payment_id=p_payment_id for update;
  if p_approve and v_pay.status='pending' and v_pay.method='qr' and v_pay.expires_at<=now() then
    perform public.fail('payment_expired','P0002');
  end if;
  perform public.verify_payment_before_terms(p_payment_id,p_approve,p_reason);
end $$;

alter function public.record_counter_payment(public.payment_kind,public.payment_method,uuid,bigint,text,text,text) rename to record_counter_payment_before_terms;
create function public.record_counter_payment(p_kind public.payment_kind,p_method public.payment_method,
  p_member_id uuid default null,p_item_id bigint default null,p_walk_in_name text default null,
  p_reference_no text default null,p_discount_code text default null) returns public.payments
language plpgsql security definer set search_path='' as $$
begin
  perform public.require_permission('payments:record');
  if p_member_id is not null then
    perform 1 from public.members where user_id=p_member_id for update;
    if p_kind in ('membership','renewal') and exists(select 1 from public.payments
      where member_id=p_member_id and status='pending' and kind in ('membership','renewal')) then
      perform public.fail('renewal_pending','P0002');
    end if;
    if p_kind='membership' and exists(select 1 from public.membership_terms
      where member_id=p_member_id and cancelled_at is null and ends_at>now()) then
      perform public.fail('already_availed','P0002');
    end if;
  end if;
  return public.record_counter_payment_before_terms(p_kind,p_method,p_member_id,p_item_id,p_walk_in_name,p_reference_no,p_discount_code);
end $$;

create function public.request_membership_refund(p_term_id bigint,p_reason text) returns public.refund_requests
language plpgsql security definer set search_path='' as $$
declare v_term public.membership_terms; v_pay public.payments; v_request public.refund_requests;
begin
  perform public.require_active_user();
  if length(btrim(coalesce(p_reason,''))) not between 1 and 1000 then perform public.fail('bad_input','22023'); end if;
  select * into v_term from public.membership_terms where term_id=p_term_id and member_id=auth.uid();
  if not found then perform public.fail('not_found','22023'); end if;
  select * into v_pay from public.payments where payment_id=v_term.payment_id for update;
  if not found or v_pay.status<>'verified' or v_pay.amount<=0 then perform public.fail('not_refundable','P0002'); end if;
  perform 1 from public.members where user_id=auth.uid() for update;
  select * into v_term from public.membership_terms where term_id=p_term_id for update;
  if v_term.cancelled_at is not null then perform public.fail('not_refundable','P0002'); end if;
  if exists(select 1 from public.refund_requests where term_id=p_term_id) then perform public.fail('already_requested','P0002'); end if;
  if v_pay.verified_at is null or now()>=v_pay.verified_at+interval '24 hours' then
    perform public.fail('refund_window_closed','22023');
  end if;
  insert into public.refund_requests(term_id,payment_id,member_id,amount,reason)
    values(p_term_id,v_pay.payment_id,auth.uid(),v_pay.amount,btrim(p_reason)) returning * into v_request;
  perform public.audit('refund.request','refund_requests',v_request.refund_request_id::text,jsonb_build_object('amount',v_pay.amount,'term_id',p_term_id));
  return v_request;
end $$;

create function public.decide_membership_refund(p_request_id bigint,p_approve boolean,p_reason text default null) returns void
language plpgsql security definer set search_path='' as $$
declare v_request public.refund_requests; v_pay public.payments;
begin
  perform public.require_active_user();
  if not public.has_role('admin') then perform public.fail('forbidden','42501'); end if;
  if p_approve is null or length(btrim(coalesce(p_reason,''))) not between 1 and 1000 then perform public.fail('bad_input','22023'); end if;
  select * into v_request from public.refund_requests where refund_request_id=p_request_id;
  if not found then perform public.fail('not_found','22023'); end if;
  if v_request.member_id=auth.uid() then perform public.fail('self_decision','42501'); end if;
  select * into v_pay from public.payments where payment_id=v_request.payment_id for update;
  perform 1 from public.members where user_id=v_request.member_id for update;
  select * into v_request from public.refund_requests where refund_request_id=p_request_id for update;
  if v_request.status<>'pending' then perform public.fail('already_processed','P0002'); end if;
  if v_pay.status<>'verified' or v_request.amount<>v_pay.amount then perform public.fail('not_refundable','P0002'); end if;
  if p_approve then
    update public.membership_terms set cancelled_at=now(),cancelled_by=auth.uid(),cancellation_reason='Refund approved'
      where term_id=v_request.term_id and cancelled_at is null;
    if not found then perform public.fail('already_processed','P0002'); end if;
    perform public.project_membership_terms(v_request.member_id);
  end if;
  update public.refund_requests set status=case when p_approve then 'approved' else 'rejected' end,
    decided_by=auth.uid(),decided_at=now(),decision_reason=btrim(p_reason) where refund_request_id=p_request_id;
  perform public.notify(v_request.member_id,'payment',case when p_approve then 'Cancellation approved; refund awaiting repayment' else 'Cancellation request rejected' end,
    btrim(p_reason),jsonb_build_object('refund_request_id',p_request_id));
  perform public.audit('refund.decide','refund_requests',p_request_id::text,jsonb_build_object('approved',p_approve,'reason',btrim(p_reason)));
end $$;

create function public.record_membership_refund_paid(p_request_id bigint,p_method public.payment_method,p_reference text default null) returns void
language plpgsql security definer set search_path='' as $$
declare v_request public.refund_requests; v_pay public.payments;
begin
  perform public.require_active_user();
  if not public.has_role('admin') then perform public.fail('forbidden','42501'); end if;
  if p_method is null or p_method='qr' or (p_method in ('gcash','bank_transfer') and coalesce(btrim(p_reference),'')='') then
    perform public.fail('bad_input','22023');
  end if;
  select * into v_request from public.refund_requests where refund_request_id=p_request_id;
  if not found then perform public.fail('not_found','22023'); end if;
  if v_request.member_id=auth.uid() then perform public.fail('self_decision','42501'); end if;
  select * into v_pay from public.payments where payment_id=v_request.payment_id for update;
  select * into v_request from public.refund_requests where refund_request_id=p_request_id for update;
  if v_request.status<>'approved' then perform public.fail('already_processed','P0002'); end if;
  if v_pay.status<>'verified' or v_request.amount<>v_pay.amount then perform public.fail('not_refundable','P0002'); end if;
  update public.refund_requests set status='paid',paid_by=auth.uid(),paid_at=now(),repayment_method=p_method,
    repayment_reference=nullif(btrim(p_reference),'') where refund_request_id=p_request_id;
  perform public.notify(v_request.member_id,'payment','Refund repaid',p_method::text,jsonb_build_object('refund_request_id',p_request_id));
  perform public.audit('refund.paid','refund_requests',p_request_id::text,
    jsonb_build_object('amount',v_request.amount,'method',p_method,'reference',nullif(btrim(p_reference),'')));
end $$;

alter function public.void_payment(bigint,text) rename to void_payment_before_terms;
create function public.void_payment(p_payment_id bigint,p_reason text) returns void
language plpgsql security definer set search_path='' as $$
declare v_pay public.payments;
begin
  perform public.require_permission('payments:void');
  select * into v_pay from public.payments where payment_id=p_payment_id for update;
  if exists(select 1 from public.refund_requests where payment_id=p_payment_id and status in ('pending','approved','paid')) then
    perform public.fail('refund_in_progress','P0002');
  end if;
  if v_pay.member_id is not null then perform 1 from public.members where user_id=v_pay.member_id for update; end if;
  if v_pay.kind in ('membership','renewal') then
    if coalesce(btrim(p_reason),'')='' then perform public.fail('bad_input','22023'); end if;
    if v_pay.status<>'verified' then perform public.fail('already_processed','P0002'); end if;
    update public.payments set status='voided',voided_by=auth.uid(),voided_at=now(),void_reason=btrim(p_reason)
      where payment_id=p_payment_id;
    update public.membership_renewals set status='cancelled' where renewal_id=v_pay.renewal_id;
    update public.membership_terms set cancelled_at=now(),cancelled_by=auth.uid(),cancellation_reason=btrim(p_reason)
      where payment_id=p_payment_id;
    perform public.project_membership_terms(v_pay.member_id);
    perform public.notify(v_pay.member_id,'payment','Payment voided',btrim(p_reason),jsonb_build_object('payment_id',p_payment_id));
    perform public.audit('payment.void','payments',p_payment_id::text,jsonb_build_object('reason',btrim(p_reason)));
  else
    perform public.void_payment_before_terms(p_payment_id,p_reason);
  end if;
end $$;

create or replace function public.revenue_summary(p_from date,p_to date)
returns table(day date,kind public.payment_kind,method public.payment_method,payments bigint,total numeric)
language plpgsql stable security definer set search_path='' as $$
begin
  perform public.require_permission('reports:revenue');
  return query select q.d,q.k,q.m,sum(q.n)::bigint,sum(q.amount) from (
    select (p.verified_at at time zone 'Asia/Manila')::date d,p.kind k,p.method m,1::bigint n,p.amount
      from public.payments p where p.status='verified'
    union all
    select (r.paid_at at time zone 'Asia/Manila')::date,p.kind,r.repayment_method,0::bigint,-r.amount
      from public.refund_requests r join public.payments p using(payment_id) where r.status='paid'
  ) q where q.d between p_from and p_to group by q.d,q.k,q.m order by q.d,q.k,q.m;
end $$;

create or replace function public.daily_cash_summary(p_day date)
returns table(kind public.payment_kind,payments bigint,total numeric)
language plpgsql stable security definer set search_path='' as $$
begin
  perform public.require_permission('reports:cash');
  return query select q.k,sum(q.n)::bigint,sum(q.amount) from (
    select p.kind k,1::bigint n,p.amount from public.payments p
      where p.status='verified' and p.method='cash' and (p.verified_at at time zone 'Asia/Manila')::date=p_day
    union all
    select p.kind,0::bigint,-r.amount from public.refund_requests r join public.payments p using(payment_id)
      where r.status='paid' and r.repayment_method='cash' and (r.paid_at at time zone 'Asia/Manila')::date=p_day
  ) q group by q.k order by q.k;
end $$;

create function public.membership_refund_outflows(p_from date,p_to date)
returns table(refund_request_id bigint,payment_id bigint,amount numeric,paid_at timestamptz,method public.payment_method,reference text)
language plpgsql stable security definer set search_path='' as $$
begin
  perform public.require_permission('reports:revenue');
  return query select r.refund_request_id,r.payment_id,r.amount,r.paid_at,r.repayment_method,r.repayment_reference
    from public.refund_requests r where r.status='paid' and (r.paid_at at time zone 'Asia/Manila')::date between p_from and p_to
    order by r.paid_at,r.refund_request_id;
end $$;

create or replace function public.expire_memberships() returns int
language plpgsql security definer set search_path='' as $$
declare v_member record; v_count int:=0;
begin
  for v_member in select m.user_id from public.members m
    where exists(select 1 from public.membership_terms t where t.member_id=m.user_id) for update of m
  loop
    if not public.is_active_member(v_member.user_id) and exists(select 1 from public.user_membership_packages
      where member_id=v_member.user_id and status='active') then
      perform public.notify(v_member.user_id,'membership','Your membership has expired');
      v_count:=v_count+1;
    end if;
    perform public.project_membership_terms(v_member.user_id);
  end loop;
  update public.user_amenities set status='expired' where status='active' and expires_at is not null and expires_at<=now();
  return v_count;
end $$;

create or replace function public.send_expiry_reminders() returns int
language plpgsql security definer set search_path='' as $$
declare v_term record; v_count int:=0;
begin
  for v_term in select t.* from public.membership_terms t where t.cancelled_at is null and t.starts_at<=now() and t.ends_at>now()
    and (t.ends_at at time zone 'Asia/Manila')::date=(now() at time zone 'Asia/Manila')::date+3
    and not exists(select 1 from public.membership_terms n where n.member_id=t.member_id and n.cancelled_at is null
      and n.starts_at<=t.ends_at and n.ends_at>t.ends_at)
    and not exists(select 1 from public.user_notifications n where n.user_id=t.member_id and n.type='membership'
      and n.reference @> jsonb_build_object('term_id',t.term_id,'reminder','3d'))
  loop
    perform public.notify(v_term.member_id,'membership','Your membership ends in 3 days',null,
      jsonb_build_object('term_id',v_term.term_id,'reminder','3d'));
    v_count:=v_count+1;
  end loop;
  return v_count;
end $$;

alter table public.membership_terms enable row level security;
alter table public.refund_requests enable row level security;
revoke all on public.membership_terms,public.refund_requests from public,anon,authenticated;
grant select on public.membership_terms,public.refund_requests to authenticated;
create policy membership_terms_read on public.membership_terms for select to authenticated
  using(public.is_active_user() and (member_id=auth.uid() or public.is_staff()));
create policy refund_requests_read on public.refund_requests for select to authenticated
  using(public.is_active_user() and (member_id=auth.uid() or public.has_role('admin')));

revoke all on function public.snapshot_membership_payment(), public.project_membership_terms(uuid),
  public.create_membership_extension(uuid,bigint,text),public.verify_payment_before_terms(bigint,boolean,text),
  public.record_counter_payment_before_terms(public.payment_kind,public.payment_method,uuid,bigint,text,text,text),
  public.void_payment_before_terms(bigint,text) from public,anon,authenticated;
revoke all on function public.extend_membership(bigint,text),public.request_membership_refund(bigint,text),
  public.decide_membership_refund(bigint,boolean,text),public.record_membership_refund_paid(bigint,public.payment_method,text),
  public.verify_payment(bigint,boolean,text),public.record_counter_payment(public.payment_kind,public.payment_method,uuid,bigint,text,text,text),
  public.void_payment(bigint,text),public.membership_refund_outflows(date,date) from public,anon,authenticated;
grant execute on function public.extend_membership(bigint,text),public.request_membership_refund(bigint,text),
  public.decide_membership_refund(bigint,boolean,text),public.record_membership_refund_paid(bigint,public.payment_method,text),
  public.verify_payment(bigint,boolean,text),public.record_counter_payment(public.payment_kind,public.payment_method,uuid,bigint,text,text,text),
  public.void_payment(bigint,text),public.membership_refund_outflows(date,date) to authenticated;
