-- Read-only duplicate preflight: supabase/preflight/0021_preflight.sql.
-- Resolve existing duplicates explicitly; never silently cancel a member's request.
do $$
begin
    if exists (
        select 1 from public.time_requests
        where status = 'pending' and requested_by = 'member' and training_session_id is null
        group by member_id, coach_id having count(*) > 1
    ) then
        raise exception 'duplicate_pending_session_requests: resolve the rows listed by 0021_preflight.sql before retrying';
    end if;
end;
$$;

-- A unique partial index protects old clients and simultaneous submissions as well as the new form.
-- Reschedule requests and coach invitations do not consume this member-created booking slot.
create unique index time_requests_one_pending_member_per_coach
    on public.time_requests (member_id, coach_id)
    where status = 'pending' and requested_by = 'member' and training_session_id is null;

create function public.request_member_time(p_member_id uuid, p_start timestamptz, p_end timestamptz)
returns bigint
language plpgsql security definer
set search_path = ''
as $$
declare
    v_uid uuid := auth.uid();
    v_id bigint;
begin
    perform public.require_active_user();
    if not exists (select 1 from public.coaches where user_id = v_uid) then
        perform public.fail('forbidden', '42501');
    end if;
    if p_member_id is null or p_start is null or p_end is null or p_end <= p_start then
        perform public.fail('bad_input', '22023');
    end if;
    -- Lock the active relationship so ending the hire cannot leave a newly-created invitation behind.
    perform 1 from public.coach_hires
    where coach_id = v_uid and member_id = p_member_id and status = 'active' for update;
    if not found then perform public.fail('no_active_hire', '22023'); end if;
    if not public.is_active_member(p_member_id) then
        perform public.fail('invite_member_inactive', '22023');
    end if;
    insert into public.time_requests (coach_id, member_id, requested_by, requested_start, requested_end)
    values (v_uid, p_member_id, 'coach', p_start, p_end)
    returning time_request_id into v_id;
    perform public.notify(p_member_id, 'time_request', 'Your coach invited you to a session', null,
        jsonb_build_object('time_request_id', v_id));
    return v_id;
end;
$$;
revoke all on function public.request_member_time(uuid, timestamptz, timestamptz) from public, anon, authenticated;
grant execute on function public.request_member_time(uuid, timestamptz, timestamptz) to authenticated;

-- The existing SELECT policy already restricts a targeted invitation to its coach/member,
-- and respond_time_request already permits only its named recipient to approve or reject.
-- Keep request_time's existing open-slot API for older/Android clients.
