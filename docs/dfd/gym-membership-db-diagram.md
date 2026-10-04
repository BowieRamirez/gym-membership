w# Untitled diagram documentation
## Summary

- [Introduction](#introduction)
- [Database Type](#database-type)
- [Table Structure](#table-structure)
	- [user_session](#user_session)
	- [users](#users)
	- [roles](#roles)
	- [permissions](#permissions)
	- [user_roles](#user_roles)
	- [role_permissions](#role_permissions)
	- [user_settings](#user_settings)
	- [payments](#payments)
	- [amenities](#amenities)
	- [membership_packages](#membership_packages)
	- [user_amenities](#user_amenities)
	- [user_membership_packages](#user_membership_packages)
	- [coaches](#coaches)
	- [members](#members)
	- [coach_hires](#coach_hires)
	- [training_sessions](#training_sessions)
	- [time_requests](#time_requests)
	- [session_attendance](#session_attendance)
	- [amenity_usages](#amenity_usages)
	- [membership_renewals](#membership_renewals)
	- [user_notifications](#user_notifications)
- [Relationships](#relationships)
- [Database Diagram](#database-diagram)

## Introduction

## Database type

- **Database system:** PostgreSQL
## Table structure

### user_session

| Name           | Type      | Settings                       | References                    | Note |
| -------------- | --------- | ------------------------------ | ----------------------------- | ---- |
| **session_id** | INTEGER   | 🔑 PK, not null, autoincrement |                               |      |
| **user_id**    | INT       | null                           | fk_user_session_user_id_users |      |
| **token**      | VARCHAR   | null, unique                   |                               |      |
| **expires_at** | TIMESTAMP | null                           |                               |      |
| **created_at** | TIMESTAMP | null, default: now()           |                               |      | 


### users
Application user accounts for authentication
| Name                  | Type      | Settings                       | References | Note |
| --------------------- | --------- | ------------------------------ | ---------- | ---- |
| **user_id**           | SERIAL    | 🔑 PK, not null, autoincrement |            |      |
| **username**          | VARCHAR   | not null, unique               |            |      |
| **email**             | VARCHAR   | not null, unique               |            |      |
| **password_hash**     | VARCHAR   | not null                       |            |      |
| **is_active**         | BOOLEAN   | not null, default: true        |            |      |
| **email_verified_at** | TIMESTAMP | null                           |            |      |
| **last_login_at**     | TIMESTAMP | null                           |            |      |
| **created_at**        | TIMESTAMP | not null, default: now()       |            |      |
| **updated_at**        | TIMESTAMP | not null, default: now()       |            |      | 


### roles
Named roles assigned to users
| Name            | Type      | Settings                       | References | Note |
| --------------- | --------- | ------------------------------ | ---------- | ---- |
| **role_id**     | SERIAL    | 🔑 PK, not null, autoincrement |            |      |
| **name**        | VARCHAR   | not null, unique               |            |      |
| **description** | TEXT      | null                           |            |      |
| **created_at**  | TIMESTAMP | not null, default: now()       |            |      | 


### permissions
Fine-grained permissions granted to roles
| Name              | Type      | Settings                       | References | Note             |
| ----------------- | --------- | ------------------------------ | ---------- | ---------------- |
| **permission_id** | SERIAL    | 🔑 PK, not null, autoincrement |            |                  |
| **name**          | VARCHAR   | not null, unique               |            | e.g. orders:read |
| **resource**      | VARCHAR   | not null                       |            |                  |
| **action**        | VARCHAR   | not null                       |            |                  |
| **description**   | TEXT      | null                           |            |                  |
| **created_at**    | TIMESTAMP | not null, default: now()       |            |                  | 


### user_roles
Many-to-many join between users and roles
| Name             | Type      | Settings                       | References                  | Note |
| ---------------- | --------- | ------------------------------ | --------------------------- | ---- |
| **user_role_id** | SERIAL    | 🔑 PK, not null, autoincrement |                             |      |
| **user_id**      | INT       | not null                       | fk_user_roles_user_id_users |      |
| **role_id**      | INT       | not null                       | fk_user_roles_role_id_roles |      |
| **assigned_at**  | TIMESTAMP | not null, default: now()       |                             |      | 


### role_permissions
Many-to-many join between roles and permissions
| Name                   | Type      | Settings                       | References                                    | Note |
| ---------------------- | --------- | ------------------------------ | --------------------------------------------- | ---- |
| **role_permission_id** | SERIAL    | 🔑 PK, not null, autoincrement |                                               |      |
| **role_id**            | INT       | not null                       | fk_role_permissions_role_id_roles             |      |
| **permission_id**      | INT       | not null                       | fk_role_permissions_permission_id_permissions |      |
| **granted_at**         | TIMESTAMP | not null, default: now()       |                                               |      | 


### user_settings
Per-user preferences and configuration (one row per user)
| Name                | Type      | Settings                       | References                     | Note                                      |
| ------------------- | --------- | ------------------------------ | ------------------------------ | ----------------------------------------- |
| **user_setting_id** | SERIAL    | 🔑 PK, not null, autoincrement |                                |                                           |
| **user_id**         | INT       | not null, unique               | fk_user_settings_user_id_users |                                           |
| **locale**          | VARCHAR   | not null, default: 'en'        |                                |                                           |
| **timezone**        | VARCHAR   | not null, default: 'UTC'       |                                |                                           |
| **theme**           | VARCHAR   | not null, default: 'system'    |                                |                                           |
| **updated_at**      | TIMESTAMP | not null, default: now()       |                                |                                           |
| **language**        | VARCHAR   | not null, default: 'en'        |                                | UI/content language code, e.g. en, es, fr | 


### payments
QR-based payments and their verification status
| Name                           | Type           | Settings                       | References                                                      | Note                                        |
| ------------------------------ | -------------- | ------------------------------ | --------------------------------------------------------------- | ------------------------------------------- |
| **payment_id**                 | SERIAL         | 🔑 PK, not null, autoincrement |                                                                 |                                             |
| **qr**                         | VARCHAR        | not null, unique               |                                                                 | QR payload/token the payer scanned          |
| **status**                     | payment_status | not null, default: 'pending'   |                                                                 | pending | verified | rejected | expired     |
| **verified_by**                | INT            | null                           | fk_payments_verified_by_users                                   | User who verified the payment               |
| **verified_at**                | TIMESTAMP      | null                           |                                                                 |                                             |
| **amount**                     | NUMERIC(12,2)  | not null                       |                                                                 |                                             |
| **created_at**                 | TIMESTAMP      | not null, default: now()       |                                                                 |                                             |
| **user_id**                    | INT            | not null                       | fk_payments_user_id_users                                       | User who made the payment                   |
| **user_membership_package_id** | INT            | null                           | fk_payments_user_membership_package_id_user_membership_packages | Membership entitlement this payment settles |
| **user_amenity_id**            | INT            | null                           | fk_payments_user_amenity_id_user_amenities                      | Amenity entitlement this payment settles    |
| **currency**                   | CHAR(3)        | not null, default: 'USD'       |                                                                 | ISO 4217 currency code                      | 


### amenities
Catalog of bookable/available amenities
| Name            | Type      | Settings                       | References | Note |
| --------------- | --------- | ------------------------------ | ---------- | ---- |
| **amenity_id**  | SERIAL    | 🔑 PK, not null, autoincrement |            |      |
| **name**        | VARCHAR   | not null, unique               |            |      |
| **description** | TEXT      | null                           |            |      |
| **is_active**   | BOOLEAN   | not null, default: true        |            |      |
| **created_at**  | TIMESTAMP | not null, default: now()       |            |      | 


### membership_packages
Catalog of purchasable membership packages
| Name                      | Type          | Settings                       | References | Note                    |
| ------------------------- | ------------- | ------------------------------ | ---------- | ----------------------- |
| **membership_package_id** | SERIAL        | 🔑 PK, not null, autoincrement |            |                         |
| **name**                  | VARCHAR       | not null, unique               |            |                         |
| **description**           | TEXT          | null                           |            |                         |
| **price**                 | NUMERIC(12,2) | not null                       |            |                         |
| **duration_days**         | INT           | not null                       |            | Validity length in days |
| **is_active**             | BOOLEAN       | not null, default: true        |            |                         |
| **created_at**            | TIMESTAMP     | not null, default: now()       |            |                         |
| **currency**              | CHAR(3)       | not null, default: 'USD'       |            | ISO 4217 currency code  | 


### user_amenities
Junction: which amenities a user has availed
| Name                | Type      | Settings                       | References                             | Note |
| ------------------- | --------- | ------------------------------ | -------------------------------------- | ---- |
| **user_amenity_id** | SERIAL    | 🔑 PK, not null, autoincrement |                                        |      |
| **user_id**         | INT       | not null                       | fk_user_amenities_user_id_users        |      |
| **amenity_id**      | INT       | not null                       | fk_user_amenities_amenity_id_amenities |      |
| **availed_at**      | TIMESTAMP | not null, default: now()       |                                        |      | 


### user_membership_packages
Junction: membership packages a user has availed
| Name                           | Type              | Settings                       | References                                                            | Note                         |
| ------------------------------ | ----------------- | ------------------------------ | --------------------------------------------------------------------- | ---------------------------- |
| **user_membership_package_id** | SERIAL            | 🔑 PK, not null, autoincrement |                                                                       |                              |
| **user_id**                    | INT               | not null                       | fk_user_membership_packages_user_id_users                             |                              |
| **membership_package_id**      | INT               | not null                       | fk_user_membership_packages_membership_package_id_membership_packages |                              |
| **starts_at**                  | TIMESTAMP         | not null                       |                                                                       |                              |
| **ends_at**                    | TIMESTAMP         | null                           |                                                                       |                              |
| **status**                     | membership_status | not null, default: 'active'    |                                                                       | active | expired | cancelled |
| **created_at**                 | TIMESTAMP         | not null, default: now()       |                                                                       |                              | 


### coaches
Coaching staff profiles
| Name             | Type          | Settings                       | References               | Note                          |
| ---------------- | ------------- | ------------------------------ | ------------------------ | ----------------------------- |
| **coach_id**     | SERIAL        | 🔑 PK, not null, autoincrement |                          |                               |
| **user_id**      | INT           | not null, unique               | fk_coaches_user_id_users | Login/identity for this coach |
| **bio**          | TEXT          | null                           |                          |                               |
| **specialty**    | VARCHAR       | null                           |                          |                               |
| **hourly_rate**  | NUMERIC(12,2) | null                           |                          |                               |
| **is_available** | BOOLEAN       | not null, default: true        |                          |                               |
| **created_at**   | TIMESTAMP     | not null, default: now()       |                          |                               | 


### members
Member profiles — gym members who may optionally hire coaches
| Name           | Type      | Settings                       | References               | Note                           |
| -------------- | --------- | ------------------------------ | ------------------------ | ------------------------------ |
| **member_id**  | SERIAL    | 🔑 PK, not null, autoincrement |                          |                                |
| **user_id**    | INT       | not null, unique               | fk_clients_user_id_users | Login/identity for this client |
| **goals**      | TEXT      | null                           |                          |                                |
| **created_at** | TIMESTAMP | not null, default: now()       |                          |                                | 


### coach_hires
Engagements: a client hiring a coach (optional per client)
| Name              | Type      | Settings                       | References                       | Note                       |
| ----------------- | --------- | ------------------------------ | -------------------------------- | -------------------------- |
| **coach_hire_id** | SERIAL    | 🔑 PK, not null, autoincrement |                                  |                            |
| **member_id**     | INT       | not null                       | fk_coach_hires_client_id_clients |                            |
| **coach_id**      | INT       | not null                       | fk_coach_hires_coach_id_coaches  |                            |
| **hired_at**      | TIMESTAMP | not null, default: now()       |                                  |                            |
| **ended_at**      | TIMESTAMP | null                           |                                  |                            |
| **status**        | VARCHAR   | not null, default: 'active'    |                                  | active | ended | cancelled | 


### training_sessions
Training sessions booked with a coach
| Name                    | Type      | Settings                       | References                             | Note                                        |
| ----------------------- | --------- | ------------------------------ | -------------------------------------- | ------------------------------------------- |
| **training_session_id** | SERIAL    | 🔑 PK, not null, autoincrement |                                        |                                             |
| **coach_id**            | INT       | not null                       | fk_training_sessions_coach_id_coaches  | Coach who manages/runs the session          |
| **member_id**           | INT       | not null                       | fk_training_sessions_client_id_clients |                                             |
| **title**               | VARCHAR   | null                           |                                        |                                             |
| **scheduled_start**     | TIMESTAMP | not null                       |                                        |                                             |
| **scheduled_end**       | TIMESTAMP | not null                       |                                        |                                             |
| **status**              | VARCHAR   | not null, default: 'scheduled' |                                        | scheduled | completed | cancelled | no_show |
| **notes**               | TEXT      | null                           |                                        |                                             |
| **created_at**          | TIMESTAMP | not null, default: now()       |                                        |                                             | 


### time_requests
Specific time windows requested or proposed by coaches and clients
| Name                | Type                | Settings                       | References                         | Note                                                                           |
| ------------------- | ------------------- | ------------------------------ | ---------------------------------- | ------------------------------------------------------------------------------ |
| **time_request_id** | SERIAL              | 🔑 PK, not null, autoincrement |                                    |                                                                                |
| **coach_id**        | INT                 | not null                       | fk_time_requests_coach_id_coaches  | Coach the request concerns                                                     |
| **member_id**       | INT                 | null                           | fk_time_requests_client_id_clients | Client the request concerns (null when the coach is just posting availability) |
| **requested_by**    | VARCHAR             | not null                       |                                    | Who raised the request: client | coach                                         |
| **requested_start** | TIMESTAMP           | not null                       |                                    |                                                                                |
| **requested_end**   | TIMESTAMP           | not null                       |                                    |                                                                                |
| **status**          | time_request_status | not null, default: 'pending'   |                                    | pending | approved | rejected | cancelled                                      |
| **message**         | TEXT                | null                           |                                    |                                                                                |
| **responded_at**    | TIMESTAMP           | null                           |                                    |                                                                                |
| **created_at**      | TIMESTAMP           | not null, default: now()       |                                    |                                                                                | 


### session_attendance
Per-client attendance for a training session
| Name                    | Type              | Settings                       | References                                                  | Note                                 |
| ----------------------- | ----------------- | ------------------------------ | ----------------------------------------------------------- | ------------------------------------ |
| **attendance_id**       | SERIAL            | 🔑 PK, not null, autoincrement |                                                             |                                      |
| **training_session_id** | INT               | not null                       | fk_session_attendance_training_session_id_training_sessions | Session this attendance belongs to   |
| **member_id**           | INT               | not null                       | fk_session_attendance_client_id_clients                     |                                      |
| **status**              | attendance_status | not null, default: 'present'   |                                                             | present | late | absent | excused    |
| **checked_in_at**       | TIMESTAMP         | null                           |                                                             |                                      |
| **recorded_by**         | INT               | null                           | fk_session_attendance_recorded_by_users                     | Coach/user who marked the attendance |
| **notes**               | TEXT              | null                           |                                                             |                                      |
| **created_at**          | TIMESTAMP         | not null, default: now()       |                                                             |                                      | 


### amenity_usages
Amenity usage records with proof and verification
| Name                 | Type      | Settings                       | References                                       | Note                                          |
| -------------------- | --------- | ------------------------------ | ------------------------------------------------ | --------------------------------------------- |
| **amenity_usage_id** | SERIAL    | 🔑 PK, not null, autoincrement |                                                  |                                               |
| **user_amenity_id**  | INT       | not null                       | fk_amenity_usages_user_amenity_id_user_amenities | The user's amenity entitlement being used     |
| **used_at**          | TIMESTAMP | not null, default: now()       |                                                  |                                               |
| **proof**            | VARCHAR   | null                           |                                                  | Proof of use (photo / receipt / QR reference) |
| **verified_by**      | INT       | null                           | fk_amenity_usages_verified_by_users              | Staff user who verified the usage             |
| **verified_at**      | TIMESTAMP | null                           |                                                  |                                               |
| **status**           | VARCHAR   | not null, default: 'pending'   |                                                  | pending | verified | rejected                 |
| **notes**            | TEXT      | null                           |                                                  |                                               | 


### membership_renewals
Renewals of a user's membership package terms
| Name                           | Type              | Settings                       | References                                                                 | Note                              |
| ------------------------------ | ----------------- | ------------------------------ | -------------------------------------------------------------------------- | --------------------------------- |
| **renewal_id**                 | SERIAL            | 🔑 PK, not null, autoincrement |                                                                            |                                   |
| **user_membership_package_id** | INT               | not null                       | fk_membership_renewals_user_membership_package_id_user_membership_packages | The membership term being renewed |
| **payment_id**                 | INT               | null                           | fk_membership_renewals_payment_id_payments                                 | Payment covering the renewal      |
| **new_starts_at**              | TIMESTAMP         | not null                       |                                                                            |                                   |
| **new_ends_at**                | TIMESTAMP         | null                           |                                                                            |                                   |
| **amount**                     | NUMERIC(12,2)     | null                           |                                                                            |                                   |
| **status**                     | membership_status | not null, default: 'pending'   |                                                                            | pending | completed | cancelled   |
| **renewed_at**                 | TIMESTAMP         | not null, default: now()       |                                                                            |                                   |
| **notes**                      | TEXT              | null                           |                                                                            |                                   |
| **currency**                   | CHAR(3)           | not null, default: 'USD'       |                                                                            | ISO 4217 currency code            | 


### user_notifications
Per-user notifications (request alerts, expiry reminders, etc.)
| Name                | Type      | Settings                       | References                          | Note                                                               |
| ------------------- | --------- | ------------------------------ | ----------------------------------- | ------------------------------------------------------------------ |
| **notification_id** | SERIAL    | 🔑 PK, not null, autoincrement |                                     |                                                                    |
| **user_id**         | INT       | not null                       | fk_user_notifications_user_id_users | Recipient                                                          |
| **type**            | VARCHAR   | not null                       |                                     | time_request | membership | payment | payout | attendance | system |
| **title**           | VARCHAR   | not null                       |                                     |                                                                    |
| **body**            | TEXT      | null                           |                                     |                                                                    |
| **reference**       | TEXT      | null                           |                                     | Optional deep-link payload, e.g. {"time_request_id": 42}           |
| **is_read**         | BOOLEAN   | not null, default: false       |                                     |                                                                    |
| **read_at**         | TIMESTAMP | null                           |                                     |                                                                    |
| **created_at**      | TIMESTAMP | not null, default: now()       |                                     |                                                                    | 


## Relationships

- **user_session to users**: many_to_one
- **user_roles to users**: many_to_one
- **user_roles to roles**: many_to_one
- **role_permissions to roles**: many_to_one
- **role_permissions to permissions**: many_to_one
- **user_settings to users**: one_to_one
- **user_amenities to users**: many_to_one
- **user_amenities to amenities**: many_to_one
- **user_membership_packages to users**: many_to_one
- **user_membership_packages to membership_packages**: many_to_one
- **payments to users**: many_to_one
- **payments to users**: many_to_one
- **payments to user_membership_packages**: many_to_one
- **payments to user_amenities**: many_to_one
- **coaches to users**: one_to_one
- **members to users**: one_to_one
- **coach_hires to members**: many_to_one
- **coach_hires to coaches**: many_to_one
- **training_sessions to coaches**: many_to_one
- **training_sessions to members**: many_to_one
- **time_requests to coaches**: many_to_one
- **time_requests to members**: many_to_one
- **session_attendance to training_sessions**: many_to_one
- **session_attendance to members**: many_to_one
- **session_attendance to users**: many_to_one
- **amenity_usages to user_amenities**: many_to_one
- **amenity_usages to users**: many_to_one
- **membership_renewals to user_membership_packages**: many_to_one
- **membership_renewals to payments**: one_to_one
- **user_notifications to users**: many_to_one

## Database Diagram

```mermaid
erDiagram
	user_session }o--|| users : references
	user_roles }o--|| users : references
	user_roles }o--|| roles : references
	role_permissions }o--|| roles : references
	role_permissions }o--|| permissions : references
	user_settings ||--|| users : references
	user_amenities }o--|| users : references
	user_amenities }o--|| amenities : references
	user_membership_packages }o--|| users : references
	user_membership_packages }o--|| membership_packages : references
	payments }o--|| users : references
	payments }o--|| users : references
	payments }o--|| user_membership_packages : references
	payments }o--|| user_amenities : references
	coaches ||--|| users : references
	members ||--|| users : references
	coach_hires }o--|| members : references
	coach_hires }o--|| coaches : references
	training_sessions }o--|| coaches : references
	training_sessions }o--|| members : references
	time_requests }o--|| coaches : references
	time_requests }o--|| members : references
	session_attendance }o--|| training_sessions : references
	session_attendance }o--|| members : references
	session_attendance }o--|| users : references
	amenity_usages }o--|| user_amenities : references
	amenity_usages }o--|| users : references
	membership_renewals }o--|| user_membership_packages : references
	membership_renewals ||--|| payments : references
	user_notifications }o--|| users : references

	user_session {
		INTEGER session_id
		INT user_id
		VARCHAR token
		TIMESTAMP expires_at
		TIMESTAMP created_at
	}

	users {
		SERIAL user_id
		VARCHAR username
		VARCHAR email
		VARCHAR password_hash
		BOOLEAN is_active
		TIMESTAMP email_verified_at
		TIMESTAMP last_login_at
		TIMESTAMP created_at
		TIMESTAMP updated_at
	}

	roles {
		SERIAL role_id
		VARCHAR name
		TEXT description
		TIMESTAMP created_at
	}

	permissions {
		SERIAL permission_id
		VARCHAR name
		VARCHAR resource
		VARCHAR action
		TEXT description
		TIMESTAMP created_at
	}

	user_roles {
		SERIAL user_role_id
		INT user_id
		INT role_id
		TIMESTAMP assigned_at
	}

	role_permissions {
		SERIAL role_permission_id
		INT role_id
		INT permission_id
		TIMESTAMP granted_at
	}

	user_settings {
		SERIAL user_setting_id
		INT user_id
		VARCHAR locale
		VARCHAR timezone
		VARCHAR theme
		TIMESTAMP updated_at
		VARCHAR language
	}

	payments {
		SERIAL payment_id
		VARCHAR qr
		payment_status status
		INT verified_by
		TIMESTAMP verified_at
		NUMERIC(12,2) amount
		TIMESTAMP created_at
		INT user_id
		INT user_membership_package_id
		INT user_amenity_id
		CHAR(3) currency
	}

	amenities {
		SERIAL amenity_id
		VARCHAR name
		TEXT description
		BOOLEAN is_active
		TIMESTAMP created_at
	}

	membership_packages {
		SERIAL membership_package_id
		VARCHAR name
		TEXT description
		NUMERIC(12,2) price
		INT duration_days
		BOOLEAN is_active
		TIMESTAMP created_at
		CHAR(3) currency
	}

	user_amenities {
		SERIAL user_amenity_id
		INT user_id
		INT amenity_id
		TIMESTAMP availed_at
	}

	user_membership_packages {
		SERIAL user_membership_package_id
		INT user_id
		INT membership_package_id
		TIMESTAMP starts_at
		TIMESTAMP ends_at
		membership_status status
		TIMESTAMP created_at
	}

	coaches {
		SERIAL coach_id
		INT user_id
		TEXT bio
		VARCHAR specialty
		NUMERIC(12,2) hourly_rate
		BOOLEAN is_available
		TIMESTAMP created_at
	}

	members {
		SERIAL member_id
		INT user_id
		TEXT goals
		TIMESTAMP created_at
	}

	coach_hires {
		SERIAL coach_hire_id
		INT member_id
		INT coach_id
		TIMESTAMP hired_at
		TIMESTAMP ended_at
		VARCHAR status
	}

	training_sessions {
		SERIAL training_session_id
		INT coach_id
		INT member_id
		VARCHAR title
		TIMESTAMP scheduled_start
		TIMESTAMP scheduled_end
		VARCHAR status
		TEXT notes
		TIMESTAMP created_at
	}

	time_requests {
		SERIAL time_request_id
		INT coach_id
		INT member_id
		VARCHAR requested_by
		TIMESTAMP requested_start
		TIMESTAMP requested_end
		time_request_status status
		TEXT message
		TIMESTAMP responded_at
		TIMESTAMP created_at
	}

	session_attendance {
		SERIAL attendance_id
		INT training_session_id
		INT member_id
		attendance_status status
		TIMESTAMP checked_in_at
		INT recorded_by
		TEXT notes
		TIMESTAMP created_at
	}

	amenity_usages {
		SERIAL amenity_usage_id
		INT user_amenity_id
		TIMESTAMP used_at
		VARCHAR proof
		INT verified_by
		TIMESTAMP verified_at
		VARCHAR status
		TEXT notes
	}

	membership_renewals {
		SERIAL renewal_id
		INT user_membership_package_id
		INT payment_id
		TIMESTAMP new_starts_at
		TIMESTAMP new_ends_at
		NUMERIC(12,2) amount
		membership_status status
		TIMESTAMP renewed_at
		TEXT notes
		CHAR(3) currency
	}

	user_notifications {
		SERIAL notification_id
		INT user_id
		VARCHAR type
		VARCHAR title
		TEXT body
		TEXT reference
		BOOLEAN is_read
		TIMESTAMP read_at
		TIMESTAMP created_at
	}
```