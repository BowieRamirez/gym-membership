# Owner fixes 2026-11-19 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (owner chose native execution for the last plans) to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

Created: 2026-11-19
Last updated: 2026-11-19
Status: BUILDING (owner: Q1 = (b), Q2 = (a), Q3 = 1 hour, Q8 Day pass under Membership; native execution). Migrations start at `0097` and take the next free number in phase order.
Branch: `owner-fixes-2026-11-19` (cut from `xendit-payments-2026-11-16` at `f68d440`). **Owner's rule: this branch is not merged into `main`.** No PR, no push to GitHub without the owner.

**Goal:** The owner's list of 2026-11-19: only people with a membership (a Day pass included) book amenities and send time requests; members can buy a Day pass online as a package; the sign-in locks after 10 wrong passwords; package buying is limited to 10 tries; Transactions get their own filters; Void payment goes; members pay online only; the change-password error says what really went wrong; members get a Payments page of their own.

**Architecture:** Reuse what exists: `is_active_member` and `book_amenity_for` (0087-0093), membership packages and terms (`membership_packages.duration_days`, `membership_terms`), the desk PIN lock pattern (0049/0062: tries counted in a table, locked for a time), `check_package_method` (0084), the audit page's `AuditFilter` (0086), `ErrorText` sentences (`Foundation.cs`), and the Cashier's 10-a-page tables. New database work: a Day pass flag on packages, two rate limits, one method guard, and dropping the void function.

**Tech Stack:** Supabase (Postgres migrations, Auth, Edge Functions), .NET MAUI (Core + App), CommunityToolkit.Mvvm, xUnit, Node database tests.

**Source:** owner's message 2026-11-19 (list pasted in chat), and their follow-up: a Day pass package members buy online, sold the same way at the desk, listed under Membership.

## How to use this plan

Same rules as `2026-11-17-desk-amenities-logs-buy-popup.md`:

- Decisions first. One phase at a time, in order; each phase ends working and committed.
- **Skills, every phase:**
  - `ponytail` full mode for the whole session (smallest diff, reuse before writing).
  - `superpowers:test-driven-development` for every rule: the failing test first, watched failing.
  - `superpowers:systematic-debugging` for the two bugs (Phases 1 and 2): reproduce before fixing.
  - `impeccable` (and `frontend-design` per CLAUDE.md) for every screen change. Staff pages are Windows only; member pages (Payments, Packages, Book an amenity) get Android and Windows, light and dark, and 1.3 font scale, with real phone layouts (two-line rows, chips), not a recolour.
  - `ponytail:ponytail-review` on each phase's diff before its commit.
  - `superpowers:verification-before-completion` before any "done".
  - A fresh whole-branch review at the end (`superpowers:requesting-code-review`).
- Push each phase's migrations live when the phase is finished (dry run first) and check live grants. Nothing goes to GitHub without the owner, and nothing merges into `main`.

| Phase | What | Needs | Status |
|---|---|---|---|
| 1 | Change password shows the real error, not "you may need an active membership" | Q7 | |
| 2 | Amenity booking and time requests need a membership (bug: booked without one) | Q1 | |
| 3 | Day pass package: members buy it online, the desk sells the same package | Q1, Q9 | |
| 4 | Members pay online only; cash is the front desk's | Q6 | |
| 5 | Remove Void payment; keep refunds, statuses, audit | Q5 | |
| 6 | Transactions get their own filters, apart from the audit log | Q4 | |
| 7 | Payments page for members, apart from My membership | Q8 | |
| 8 | Rate limits: 10 package buys, 10 wrong sign-ins then 5 minutes locked | Q2, Q3 | |
| 9 | Whole-branch checks, review, owner hand checks | | |

## 1. Findings (2026-11-19)

| Item | Today | Consequence |
|---|---|---|
| 1 Amenity without membership | The member path of `book_amenity_for` (0090, patched by 0093) refuses with `no_active_membership` unless `is_active_member` (0018: a current `membership_terms` row). The demo gateway checks the same. Yet the owner booked with no membership and no Day pass. Book an amenity is not a `members-only` page (`MemberShell.xaml`). | Reproduce on the real project first (which account, which path: pass, coach, paid). |
| 2 Time requests | Time requests is `members-only` (`MemberShell.xaml:25`); the request functions (0021, 0031, 0074) check `is_active_member`. A Day pass today is a walk-in payment (`walk_in_price`, 0005), not a membership term, so a Day pass holder is shut out. | Once a Day pass is a package (Phase 3) it makes a membership term, and the existing `is_active_member` checks let Day pass holders in with no new access rule. |
| 3 Day pass | The Cashier's "Day pass" kind (`StaffViewModels.cs:142`) records a `walk_in` payment at `gym_settings.walk_in_price`, with a typed name, against no package. Packages have `duration_days` (0002) and `user_membership_packages_one_active` allows one active package per member. | A Day pass package needs to end at Manila midnight (not 24 hours later) and must not clash with an active membership. |
| 4 Sign-in lock | The app signs in straight to Supabase Auth (`SupabaseGateway.SignInAsync`, `c.Auth.SignIn`). Nothing counts wrong passwords per account; only Supabase's per-IP limit applies. | A lock needs a server-side count per account (Q2). The change-password check (`ChangePasswordAsync` signs in with the current password) goes through the same door and must count too. |
| 5 Package spam | `avail_membership` / `extend_membership` (0084) make a pending payment each call; `cancel_pending_payment` (0019) cancels it. No limit on how often. | Count the member's new package payments in a window (Q3). |
| 6 Transactions filters | `AuditViewModel` (`StaffViewModels.cs:2122`) has **one** `_applied` filter shared by Audit log and Transactions (`_applied with { Money = ShowMoney }`): area, object, user, dates. The audit areas mean nothing on Transactions. | Each tab keeps its own filter; Transactions gets money filters (Q4). |
| 7 Void | `VoidPaymentsPage` sits inside Refunds (`RefundsPage.xaml.cs:9`), `void_payment(bigint,text)` (0018) is granted to authenticated. Statuses in use: `pending`, `verified`, `rejected`, `expired`, `voided`. | Remove the page and the function; old `voided` rows stay readable (Q5). |
| 8 Cash for members | Packages Buy popup step 2 offers Online / Cash at the front desk (0084, Z7); `check_package_method` allows `cash` for members; `book_amenity` takes `p_method`. | Members: online only, refused in the database too (Q6). |
| 9 Password message | `ChangePasswordAsync` signs in with the current password, then `Auth.Update`. A Supabase Auth failure body is JSON with `msg` / `error_code` (e.g. `same_password`, `weak_password`), but `ErrorText.ServerMessage` only reads `message`, so no sentence matches and the Validation fallback shows "That isn't allowed right now. For example, you may need an active membership." The owner typed their current password as the new one, so `same_password` is the likely case. | Read Auth's body properly; drop the membership example from the generic text (Q7). |
| 10 Payment history | Payments are listed on My membership (`MyMembershipPage.xaml` `PaymentCard`, Payments waiting). No page of the member's own payment history across packages and amenities. | New member page (Q8). |

## 2. Decisions

| # | Decision | Answer |
|---|---|---|
| Q1 | **Who may book an amenity and send a time request:** an account with an active membership, and a Day pass counts because it becomes a 1-day package (Phase 3). No separate Day pass rule. Anyone else gets the existing `no_active_membership` sentence and the locked page. The desk's own booking (`desk_book_amenity`, Z1) stays open to any member account. | Owner, 2026-11-19: (b) |
| Q2 | **Sign-in lock:** 10 wrong passwords for one account → that account can't sign in for **5 minutes**, then the count starts again; a right password resets it. Counted per account, not per device. Message: "Too many wrong tries. Try again in N minutes." How: (a) Supabase's **Password Verification Attempt** Auth Hook (a Postgres function Supabase calls on every password check; nothing gets round it) if the project's plan has it; (b) otherwise a `sign-in` Edge Function the app calls instead of `Auth.SignIn` (same count; Supabase's per-IP limit still covers anyone calling Auth directly). Recommended: (a), check the plan first. | Owner, 2026-11-19: (a) |
| Q3 | **Package buy limit:** at most **10 new package payments per member in a rolling hour** (made, then cancelled or expired, all count; Day pass included). The 11th: "You've tried 10 times. Try again in N minutes." | Owner, 2026-11-19: 1 hour |
| Q4 | **Transactions filters**, own state per tab. Audit log keeps all its filters (area, object, user, dates). Transactions: dates, customer (name search), What (Membership, Amenity, Day pass, Refund, Cash shift), Paid with (Cash, GCash, Maya, QR Ph, Card), Status, Recorded by. Clear filters and Export CSV act on the open tab only. | Claude's call |
| Q5 | **Void goes:** `VoidPaymentsPage` and its view model removed, Refunds shows refunds only, `void_payment` dropped. Old `voided` rows still show in history and logs as "Voided". Refunds stay. Status words on screen: Pending, Paid (`verified`), Failed (`rejected`, `expired`), Refunded (a payment with a finished refund), Voided (history only). The database values don't change. | Claude's call |
| Q6 | **Members pay online only.** Packages popup: step 2 (Online / Cash) goes, Buy goes straight to online. Book an amenity: no cash choice. In the database, `check_package_method` and `book_amenity` refuse `cash` from a member (`online_only`). Members' cash payments already waiting stay for the desk to verify or expire (0085). The desk's cash flows don't change; each cash payment's audit row already names the staff member (actor, 0078); a test pins it. | From the owner's list |
| Q7 | **Password errors:** `ErrorText` reads Supabase Auth's `msg` / `error_code` too, so `wrong_password`, `same_password` and `weak_password` show their own sentence (add `weak_password`). The generic Validation text loses its example: "That isn't allowed right now." Membership never matters for changing a password. | Claude's call |
| Q8 | **Payments page (members):** a new "Payments" menu item for any member account (not `members-only`). Lists only the member's own payments, newest first, 10 a page: date, what (package or amenity name), amount, paid with, status, payment code. Filter chips: All, Membership (Day pass included: it's a package), Amenity. My membership keeps only the waiting payment's actions (Pay online, Cancel); its payment list moves here. Read from `payments` under the existing own-rows rule (check RLS, no new function). | Owner, 2026-11-19 |
| Q9 | **Day pass package.** `membership_packages.is_day_pass boolean not null default false`, at most one on sale (partial unique index). Its term starts when the payment is verified and ends at **midnight Manila that day** (not 24 hours later). A member with an active membership can't buy one (`already_member`); a Day pass doesn't stack or extend. Admins set its name, price and description on the Packages admin page like any package (a "Day pass" switch). Members see it on Packages like any package, paid online. **The desk:** the Cashier's "Day pass" kind with a member picked sells that package to them (a cash or desk-online payment, which makes the term); with no member (a walk-in with no account) it stays a `walk_in` payment with a typed name, as today, at the Day pass package's price. `gym_settings.walk_in_price` is no longer read (left in place, not dropped). | Claude's call, from the owner's (b) |

## Global Constraints

- Migrations start at `0097`, next free number in phase order. Every new or recreated function revokes `public, anon, authenticated` explicitly and grants only who needs it; check live grants after each push.
- The front desk still creates **only** member accounts; only front desk and admins record cash.
- No IP addresses in any log (0083).
- Manila time for "today" and "midnight" (`Asia/Manila`), as the demo seeds do.
- `src/GymMembership.App/Resources/Raw/supabase.json` points at the real project: move it aside only for demo builds and put it back at once.
- Demo gateway mirrors every new rule (`DemoGateway`, `DemoReservations`, `DemoRequests`, `DemoSubscriptions`).
- This branch never merges into `main`.

## Review Focus

1. **Day pass bought at 11:30 pm** → it ends at midnight, so the member can't book a slot after midnight; the Buy popup says it ends tonight.
2. **10th wrong password, then the right one within 5 minutes** → still locked; after 5 minutes the right one works and the count is back to 0.
3. **Change password with a wrong current password 10 times** → the account locks like sign-in (same door); the message says locked, not "wrong password".
4. **Old app (v0.8.0) sends `cash` for a package** → refused with `online_only` and its sentence; nothing is created.
5. **Transactions filter set, switch to Audit log and back** → each tab still shows its own filters and rows; Export CSV exports the open tab's rows only.

---

## Phase 1: Password message (Q7)

### Task 1: The real reason shows

**Files:** `src/GymMembership.Core/Services/Foundation.cs` (`ErrorText.ServerMessage`: also read `msg` and `error_code`; `For(AppErrorKind.Validation)` text; `weak_password` sentence; `ErrorMapper` token `weak_password`), `src/GymMembership.Core/Backend/SupabaseGateway.cs:137` only if reproduction shows another cause, test `tests/GymMembership.Tests/AccountTests.cs`.

- [ ] Reproduce on the real project (demo build won't show it): change password to the same password; note the raw body.
- [ ] Failing tests: `ErrorText.For(Validation, "{\"code\":422,\"error_code\":\"same_password\",\"msg\":\"New password should be different from the old password.\"}")` → "Pick a password that's different from your old one."; same for `weak_password`; `ErrorText.For(Validation)` doesn't contain "membership".
- [ ] Fix, tests pass, `ponytail:ponytail-review`, commit.

## Phase 2: Who may book (Q1)

### Task 2: Close the hole

**Files:** found by reproduction; likely `book_amenity_for` (latest body: 0090 + 0093 patch) or the app's Book an amenity path; `src/GymMembership.App/MemberShell.xaml(.cs)` and `PhoneNavigation.cs` (Book an amenity becomes `members-only`), `supabase/tests/local/access.mjs`, test `tests/GymMembership.Tests/AccessTests.cs`.

- [ ] Reproduce the owner's booking first (systematic-debugging): which account, which path (pass, coach, paid, down payment).
- [ ] Failing tests: an account with no membership → `no_active_membership` for booking and for a time request; the desk's booking for the same member still works.
- [ ] Fix at the root (one guard in the shared function, not per caller), migration if the database needs it, push (dry run first), live grants, screens checked, commit.

## Phase 3: Day pass package (Q1, Q9)

### Task 3: The package and its term

**Files:** next migration `00xx_day_pass_package.sql` (`is_day_pass` column and partial unique index on sale; activation sets `ends_at` to the next Manila midnight for a Day pass; `avail_membership` refuses a Day pass with `already_member` when `is_active_member`; `extend_membership` refuses a Day pass; the walk-in price reads the Day pass package), `supabase/tests/local/day-pass.mjs`, `Models.cs` (`MembershipPackage.IsDayPass`), `Foundation.cs` (`already_member`: "You already have a membership, so you don't need a Day pass."), demo mirror, test `tests/GymMembership.Tests/DayPassPackageTests.cs`.

- [ ] Failing tests: bought and verified at 9 am → active until midnight Manila, can book an amenity and send a time request; at 12:01 am → not active; member with a membership → `already_member`; two Day pass packages on sale → refused.
- [ ] Migration, push, grants, commit.

### Task 4: Admin, member and desk screens

**Files:** Packages admin page and view model ("Day pass" switch), `PackagesPage.xaml` (Day pass shows "Today only, ends at midnight"), `CashierViewModel` (`Kind == "Day pass"` with a `Member` sells the Day pass package; without one stays `walk_in`; price from the package), tests `DayPassNameTests.cs` updated, `CashierDayPassTests.cs`.

- [ ] Tests first, then screens (`impeccable`): Packages on Android and Windows, light/dark, 1.3 font scale; Cashier and admin Windows only. Commit.

## Phase 4: Online only for members (Q6)

### Task 5: Database refuses member cash

**Files:** next migration `00xx_members_online_only.sql` (`check_package_method` and `book_amenity`: `cash` from a member → `online_only`), `supabase/tests/local/online-only.mjs` (also: a desk cash payment's audit row names the staff actor), `Foundation.cs` (`online_only`: "Pay online here, or pay cash at the front desk and they'll record it."), demo mirror.

- [ ] Failing tests, migration, push, grants, commit.

### Task 6: Screens

**Files:** `PackagesViewModel` / `PackagesPage.xaml` (Buy popup loses step 2; `Method` always online), Book an amenity's `PayOption` (cash gone), `MyMembershipViewModel` notices, tests `BuyPopupTests.cs`, `CashPackageTests.cs` updated.

- [ ] Tests first, then screens (`impeccable`), Android and Windows, light/dark, 1.3 font scale, commit.

## Phase 5: Remove Void (Q5)

### Task 7: Void out, statuses in plain words

**Files:** delete `VoidPaymentsPage.xaml(.cs)` and `VoidPaymentsViewModel`; `RefundsPage.xaml(.cs)` (no Part switch), DI registration, next migration `00xx_drop_void_payment.sql` (`drop function public.void_payment(bigint, text)`), demo `void_payment` case removed, one status label helper reused by Payments (Phase 7) and Transactions (Phase 6): `PaymentStatusText(Payment) -> string`, test `tests/GymMembership.Tests/PaymentStatusTests.cs`.

- [ ] Failing test for the labels (Pending, Paid, Failed, Refunded, Voided), remove, build, tests, push, commit.

## Phase 6: Transactions filters (Q4)

### Task 8: Two filters, one per tab

**Files:** `AuditViewModel` (`_auditFilter`, `_moneyFilter`; money fields: `What`, `PaidWith`, `Status`, `Customer`, `RecordedBy`, dates), `AuditFilter` in `Services.cs` (money fields), `audit_log_page` + export (next migration: filter by those fields when `p_money`; Day pass rows = `walk_in` payments and Day pass package payments), `AuditPage.xaml` (filter row per tab), test `tests/GymMembership.Tests/TransactionsLogTests.cs` (Review Focus 5).

- [ ] Failing tests, migration and push, screen (`impeccable`, Windows only), commit.

## Phase 7: Payments page (Q8)

### Task 9: My payments

**Files:** `PaymentsViewModel` in `MemberViewModels.cs` (`Kind` filter All / Membership / Amenity, Day pass under Membership; `Page`, 10 a page, own rows only), `src/GymMembership.App/Views/PaymentsPage.xaml(.cs)`, `MemberShell.xaml`, `PhoneNavigation.cs`, `IconRail.Windows.cs` (icon), `MyMembershipPage.xaml` (payment list out, waiting payment card stays), test `tests/GymMembership.Tests/MyPaymentsTests.cs` (another member's payment never shows; filter works; a Day pass shows under Membership).

- [ ] Check the `payments` select policy only returns own rows for members (database test).
- [ ] Tests first, page with `impeccable`: phone two-line rows (what + amount, date + status chip), Windows table; light/dark, 1.3 font scale. Commit.

## Phase 8: Rate limits (Q2, Q3)

### Task 10: Package buys

**Files:** next migration `00xx_package_buy_limit.sql` (inside `avail_membership` / `extend_membership`: count the caller's package payments created in the last hour; 10 or more → `too_many_tries` with minutes left), `supabase/tests/local/buy-limit.mjs`, `Foundation.cs` sentence, demo mirror.

- [ ] Failing test (11th refused, after the hour allowed), migration, push, commit.

### Task 11: Sign-in lock (per Q2)

**Files (a):** next migration `00xx_sign_in_lock.sql` (`login_attempts(user_id, wrong, locked_until)`, hook function `password_verification_hook(event jsonb) returns jsonb`: reject while locked, count wrong, lock 5 min at 10, reset on success), enable the hook in the Supabase dashboard (owner step) and `supabase/config.toml`. **(b):** `supabase/functions/sign-in/index.ts` + the same table, `SupabaseGateway.SignInAsync` and `ChangePasswordAsync` call it. Both: `Foundation.cs` (`account_locked`: "Too many wrong tries. Try again in N minutes."), demo mirror, tests `supabase/tests/local/sign-in-lock.mjs`, `tests/GymMembership.Tests/SignInLockTests.cs` (Review Focus 2, 3).

- [ ] Failing tests, build, push, owner enables the hook (a), live check with a test account, commit.

## Phase 9: Checks

- [ ] Whole test suite and database tests green; live grants checked for every migration on this branch.
- [ ] `superpowers:requesting-code-review` on the whole branch; fixes committed.
- [ ] Owner hand checks: each finding above on the real project, Android and Windows.
- [ ] Branch stays off `main`.

## Session log

- 2026-11-19: plan written, branch `owner-fixes-2026-11-19` made.
- 2026-11-19: owner chose Q1 = (b) (Day pass as a package, desk too) and Day pass under Membership (Q8); Phase 3 added. Owner: Q2 = (a), Q3 = 1 hour, build it natively.
