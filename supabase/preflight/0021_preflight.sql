-- Read only. An empty result means the new unique index has no existing duplicate groups.
select member_id, coach_id, count(*) as pending_requests,
       array_agg(time_request_id order by time_request_id) as request_ids
from public.time_requests
where status = 'pending' and requested_by = 'member' and training_session_id is null
group by member_id, coach_id
having count(*) > 1;
-- Review these requests with the owner; cancel/reject extras through existing authorized
-- operations before deployment. Do not delete history or automatically choose a request.
