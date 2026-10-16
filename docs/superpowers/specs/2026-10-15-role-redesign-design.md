# Role redesign: Member, Front Desk, Coach

| | |
| --- | --- |
| **Date** | 2026-10-15 |
| **Status** | Draft for review. Nothing in this spec has been built or migrated. |
| **Source** | Design change brief pasted by the owner on 2026-10-15; decisions below were agreed in chat the same day. |

## 1. Goal

Give each role (member, front desk, coach) a clear, modern, interactive interface on one shared design system, keeping every current feature. Add: a notification bell, a Messenger-style chat, live amenity status, profile pictures, a coach attendance calendar, a POS-style cashier, and a subscription section with upgrade, cancel and refund request.

## 2. Decisions (agreed 2026-10-15)

| # | Question | Decision |
| --- | --- | --- |
| 1 | Amenities | Included with membership, no longer bought. The purchase and visit-logging UI is hidden. The old tables and functions (`user_amenities`, `amenity_usages`, `avail_amenity`, `verify_amenity_usage`) stay in the database untouched, so no data is lost. |
| 2 | Refunds | A member files a refund request; **admin only** approves. The money is paid back by hand; the system only records the request and the decision. |
| 3 | Downgrade | Not built. Upgrade and cancel only. |
| 4 | Amenity alerts | Members get notified when an amenity they **saved** changes status. "Recently viewed" is dropped (it would need view tracking). |
| 5 | POS | One item per order for now (existing `record_counter_payment` holds one item). A multi-item cart is a later phase and needs a `payment_items` table. |
| 6 | Live updates | Supabase Realtime for chat, notifications and amenity status; typing and online status through Realtime presence (no tables). Fallback if Realtime proves unreliable on a device: poll every 10 seconds. |
| 7 | Coach photo | Required means a persistent banner prompt on the coach's profile and home. A coach is not blocked from hires. |
| 8 | Chat photos | Kept: members and coaches can send photos to each other, and members to front desk, in both directions. |

## 3. Database changes

New migrations `0013` onward. Everything is additive; nothing is dropped. **`0012` (names letters only) is not yet on the real Supabase project and must be pushed first.**

### 3.1 New tables (4)

| Table | Purpose | Key columns |
| --- | --- | --- |
| `amenity_follows` | A member saved an amenity | `member_id`, `amenity_id` (primary key together), `created_at` |
| `support_messages` | Member to front desk chat | `message_id`, `member_id` (the thread owner), `sender_id`, `body`, `attachment_path`, `sent_at`, `delivered_at`, `read_at` |
| `support_threads` | Which staff member picked up a conversation | `member_id` (primary key), `claimed_by`, `status` (open or closed), `last_message_at` |
| `refund_requests` | Cancel and refund | `refund_id`, `member_id`, `user_membership_package_id`, `payment_id`, `reason`, `status` (pending, approved, rejected), `decided_by`, `decided_at`, `amount`, `created_at` |

### 3.2 Changes to existing tables (columns only)

| Table | New columns |
| --- | --- |
| `profiles` | `avatar_path` (set only through the function `set_my_avatar`, not a widened column grant, so a path outside the user's own folder is refused); optional `last_seen_at` (for "last active"). |
| `amenities` | `status` (new enum `available`, `in_use`, `unavailable`; default `available`), `status_reason`, `back_at`, `status_changed_by`, `status_changed_at`, `image_path` |
| `membership_packages` | `image_path` |
| `messages` | `delivered_at`, `attachment_path`. Relax the "body not empty" check to "body or attachment". Extend the insert and update column grants. |
| `gym_settings` | refund terms: `refund_window_days`, `refund_percent` |

### 3.3 Functions, types, permissions, storage

| Item | Detail |
| --- | --- |
| `set_amenity_status(amenity, status, reason, back_at)` | Checks permission, updates the amenity, writes `audit_log`, notifies followers, returns the list of notified members (count and names) for the staff confirmation. |
| `upgrade_membership(new_package)` | Creates the price-difference payment and the upgraded membership. Rules for credit on remaining days are fixed in the phase 7 plan. |
| `cancel_membership(ump)` and `request_refund`, `decide_refund` | Cancel a membership; file and decide a refund. `decide_refund` requires `refunds:decide`. |
| `set_my_avatar(path)` | Sets or clears the caller's picture; refuses a path outside their own folder and inactive users. Built in phase 1 (migration `0013`). |
| `mark_delivered(...)` | Marks a conversation's messages as delivered when the recipient's app receives them. |
| Triggers | Notify staff on a new support message, a rejected or expired payment, and an amenity set to unavailable. Notify followers on amenity status change. |
| Enum | `notification_type` gains `amenity` (and `support` if needed). |
| Permissions | New: `amenities:set_status` (admin, employee), `support:reply` (admin, employee), `refunds:decide` (admin). Added in reference data and `role_permissions`. |
| Storage | New buckets `avatars` (user writes own folder, all signed-in users read) and `chat-attachments` (path starts with the conversation's ids; only the two participants read). Images only, 5 MB, same limits as the existing buckets. Package and amenity images reuse `gym-assets`. |
| Realtime | Add `messages`, `support_messages`, `user_notifications`, `amenities` to the `supabase_realtime` publication. |

### 3.4 No database change needed

Notification bell and "mark all as read" (one filtered update on `read_at`), the coach attendance calendar and its summary (`training_sessions` + `session_attendance`; `record_session_attendance` already lets a coach correct a status), coach search, sign-out placement, status chips, light and dark mode, dashboard counts.

### 3.5 Privacy rules

- A member sees front desk as one generic "Slim & Shape front desk" identity; individual staff profiles stay hidden (no change to the `profiles` select policy).
- Coach and member chats stay private to the two people; staff cannot read them.
- Support chats are readable by the member and by staff with `support:reply`.

## 4. App changes by phase

Each phase is its own branch; it merges to `main` only when the owner says so. UI phases invoke the frontend-design skill. Every phase mirrors new tables and functions in `DemoGateway` and adds xUnit and local database tests.

1. **Design system and shell.** One primary color, one accent, semantic success, warning and error colors, a type scale, WCAG AA contrast in light and dark. Shared controls: avatar (picture or initials), status chip, person and service card, header bar with the bell and an always-visible sign-out. Front desk gets a persistent bottom bar (phones) or the locked side menu (Windows). Profile picture upload for all roles. *Migration: avatars and image columns.* **Built in phase 1:** the existing design tokens were kept and guarded by a contrast test (one color darkened); the status chip already existed, so its new statuses (available, in use, unavailable) and the person and service cards arrive with phases 3 and 6; the bell opens the existing Alerts page until the phase 2 panel; the header bell is a vector drawing, not an image file.
2. **Notification bell.** Header icon with unread badge; a panel listing newest first, unread highlighted, "mark all as read". Replaces the Alerts page. **Built in phase 2:** no database change; the panel is a toolkit Popup (dropdown on desktop, full-width sheet on phones); "mark all as read" is one filtered update; the bell refreshes on any change in the app, on every page change and once a minute; Realtime moves to phase 4 with the messenger; alert deep links and the staff alert sources arrive with phases 3 and 4.
3. **Amenity Status.** Member view: cards with image or icon and a live status chip (Available, In use, Unavailable), reason and estimated return, a save (follow) toggle. Front desk: "Amenity Status" page (renamed from "Amenity visits") with a status toggle and reason, and a confirmation of who was notified. Coach: the same cards, read-only. Removes the "Buy" flow from view. *Migration: amenity status, follows, `set_amenity_status`, triggers.* **Built in phase 3:** migration 0014 (status columns writable only by set_amenity_status, amenity_follows, set_amenity_followed, permission amenities:set_status, alert type amenity); followers are alerted when an amenity becomes available or unavailable (not for "in use"); the staff page shows who was alerted; coaches get the same board read-only; the old purchase flow is hidden, its tables and functions untouched; amenity photos wait for the image upload in phase 6; the pages refresh every 30 seconds until Realtime in phase 4.
4. **Messenger.** One chat UI for all roles: conversation list, bubbles, avatars, timestamps, unread badges, online status, typing indicator, sticky input with photo and send, message ticks (sent, delivered, read). Members get Coach and Staff tabs; front desk can pick up member conversations; coaches get a search bar over trainees and conversations. First task: reproduce and fix the coach conversation that does not load reliably (systematic debugging before any rewrite). *Migration: message columns, support tables, chat bucket, Realtime.* **Built in phase 4a (plan 2026-10-16):** migration 0015 (messages.delivered_at, support_threads, support_messages, permission support:reply, and the functions chat_threads, chat_messages, mark_chat_read, mark_chats_delivered, send_support_message, claim_support_thread, set_support_status, chat_search); one Messenger for all roles with list and chat, Coach and Staff tabs for members (guests can write to the front desk), a front desk inbox that can take, release, close and reopen conversations, Sent/Delivered/Read under what you sent, search by name and message text, paged history (the old unbounded reads and the wrong-conversation race are gone), and a 6-second look-again while the page is open. **Built in phase 4b:** migration 0016 (private chat-attachments bucket, attachment_path on messages and support_messages with a "text or photo" rule and a check that the photo belongs to that conversation, can_use_chat_file and its two storage policies, and chat_threads, chat_messages and send_support_message updated); one photo per message with an optional caption in coach chats and the front desk thread, both directions; the list says "Photo" for a photo-only message; guests and ended hires can still read old photos; staff and admins cannot read coach chat photos. Photos come from the gallery picker; tap-to-enlarge is not built. Realtime, typing and online status are plan 4c.
5. **Front desk home and cashier.** Dashboard tiles with live counts (check-ins, payments to verify, amenity status, messages). "Counter payment" becomes "Cashier": service grid, order panel, total, payment method, a prominent "Complete payment". Icon based. No database change.
6. **Coach.** Attendance calendar (month and week), check mark, X, neutral days, tap a day for the session list and a correction option, summary (sessions this month, attendance rate, upcoming). Required photo prompt. Visual coach and package cards on the member side.
7. **Subscription.** Current plan at the top (name, price, renewal date, status), plan comparison with price difference, upgrade as the primary action, cancel and refund request separated at the bottom with a confirmation that states the refund terms. Admin refund queue. *Migration: upgrade, cancel, refund table, gym_settings terms.* Last because it moves money.

## 5. Testing and verification

- Each migration: local embedded-Postgres tests (`npm test` in `supabase/tests/local`) for RLS and function rules, including that members cannot read other members' support chats and that only admin can decide refunds.
- Each phase: xUnit tests for view models and gateway behavior, a live Supabase client check against the real project after its migration is pushed, and a visual check on the Android emulator and Windows.
- Realtime is verified on a real device before it is relied on; if it fails, the 10 second polling fallback ships instead.
- Existing checks stay green: 143 xUnit, 57 database checks, the Tests, Android and Windows workflows.

## 6. Risks

| Risk | Handling |
| --- | --- |
| Large scope across all three roles | Seven independent phases; the app stays releasable after each. |
| Realtime on MAUI (currently `AutoConnectRealtime = false`) | Device test first; polling fallback. |
| Refund and upgrade logic moves money | Last phase, admin-only approval, tests for every rule, nothing automatic. |
| Production database drift | Push migrations in order; run `0012` first; the CI smoke check confirms the project answers. |
| Hiding the amenity purchase flow | Old tables stay, so it can come back without data loss. |
| Free-tier Supabase pausing | Already an open item for the whole project, not new here. |

## 6a. Phase 4c status (16 Oct 2026)

Built per `2026-10-16-phase4c-realtime-design.md` with migration `0017` (applied). Changes from this document: last active is a locked table `user_last_seen`, not a `profiles.last_seen_at` column (migration `0006` grants `select` on every table, so a column would have been readable), and it is shown only while a coach hire is current. Realtime was checked against the real project with a script, and the owner confirmed it works on real devices. Bell and amenity-status Realtime remain for later.

## 6b. Phase 5 status (17 Oct 2026)

Built per `docs/superpowers/plans/2026-10-17-role-redesign-phase5.md`: a Home screen (four tiles; front desk and admin) and the Cashier (the Counter payment, redesigned), no database change. Differences from the description above: the Cashier total shows the item's price **before** any discount (the server applies the discount code when the payment is completed; a preview would need a new database function), a walk-in's total reads "Day pass", and the live counts refresh on a 20 second timer rather than through Realtime. Done: the owner confirmed on 17 Oct 2026 that Home and the Cashier work on Windows (after a crash from the icon outlines was found and fixed). Not separately recorded: the phone layout, and a small payment on the real project.

## 6c. Phase 6a status (17 Oct 2026)

Phase 6 is split: **6a** (the coach attendance calendar) is built per `2026-10-17-phase6a-attendance-calendar-design.md`, with no database change, and is waiting for a look on Windows and a phone. **6b** (coach photo prompt, visual coach and package cards, photos for amenities and packages) is not started and gets its own spec; the `image_path` columns already exist, and photo upload is admin-only today because of the `gym-assets` storage policy.

## 7. Not in scope

Downgrade, multi-item POS cart, "recently viewed" tracking, iOS and Mac, store listings, push notifications while the app is closed (bell and Realtime work only while the app is open).
