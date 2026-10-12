-- 0001: extensions and enum types
create extension if not exists btree_gist with schema extensions;
create extension if not exists pg_cron    with schema pg_catalog;

create type membership_status    as enum ('pending', 'active', 'expired', 'cancelled');
create type renewal_status       as enum ('pending', 'completed', 'cancelled');
create type payment_status       as enum ('pending', 'verified', 'rejected', 'expired', 'voided');
create type payment_method       as enum ('qr', 'cash', 'gcash', 'bank_transfer');
create type payment_kind         as enum ('membership', 'renewal', 'amenity', 'walk_in');
create type coach_hire_status    as enum ('active', 'ended', 'cancelled');
create type time_request_status  as enum ('pending', 'approved', 'rejected', 'cancelled');
create type requested_by_role    as enum ('member', 'coach');
create type session_status       as enum ('scheduled', 'completed', 'cancelled', 'no_show');
create type attendance_status    as enum ('present', 'late', 'absent', 'excused');
create type shift_status         as enum ('scheduled', 'cancelled');
create type amenity_usage_status as enum ('pending', 'verified', 'rejected');
create type notification_type    as enum ('time_request', 'membership', 'payment', 'attendance', 'message', 'system');
