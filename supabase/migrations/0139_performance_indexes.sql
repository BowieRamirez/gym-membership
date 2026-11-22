-- Performance indexes for frequent page-load and auto-refresh queries.
-- Safe and additive (IF NOT EXISTS).

create index if not exists user_notifications_unread_idx
	on public.user_notifications (user_id, created_at desc)
	where read_at is null;

create index if not exists gym_check_ins_checked_in_at_idx
	on public.gym_check_ins (checked_in_at);

create index if not exists payments_status_method_created_idx
	on public.payments (status, method, created_at);

create index if not exists reservations_status_ends_at_idx
	on public.reservations (status, ends_at);

create index if not exists time_requests_status_start_idx
	on public.time_requests (status, requested_start);

create index if not exists shift_posts_status_ends_at_idx
	on public.shift_posts (status, ends_at);
