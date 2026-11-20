# Owner fixes 2026-11-19 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (owner chose native execution for the last plans) to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

Created: 2026-11-19
Last updated: 2026-11-20
Status: BUILT 2026-11-19/20 (owner: Q1 = (b), Q2 = (a) but the hook needs the Team plan, so (b) was built; Q3 = 1 hour; Q12 = both places + Cashier; Q13 = 20 in 10 minutes; native execution). Phases 1-8, 10-11 + follow-ups done; `0097`-`0108` live, `sign-in` function redeployed. Pushed up to `ae14404`; follow-up commits (`183d696`..`0d694bf`) are local on `owner-fixes-2026-11-19`. Left: owner checks the Auth per-IP sign-in limit, adds/publishes the Day pass package live with the Day pass switch, hand checks on devices, and decides on duplicate Xendit rows in Transactions. Next migration `0109`.
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
| 1 | Change password shows the real error, not "you may need an active membership" | Q7 | DONE (`b9bde16`) |
| 2 | Amenity booking and time requests need a membership (bug: booked without one) | Q1 | DEPLOYED (`0097`, `dfa9060`) |
| 3 | Day pass package: members buy it online, the desk sells the same package | Q1, Q9 | DEPLOYED (`0098`, `11eff23`) |
| 4 | Members pay online only; cash is the front desk's | Q6 | DEPLOYED (`0099`, `eb43d31`) |
| 5 | Remove Void payment; keep refunds, statuses, audit | Q5 | DEPLOYED (`0100`, `1d129b5`) |
| 6 | Transactions get their own filters, apart from the audit log | Q4 | DEPLOYED (`0101`, `14e07c4`) |
| 7 | Payments page for members, apart from My membership | Q8 | DONE (`c10d477`) |
| 8 | Rate limits: 10 package buys, 10 wrong sign-ins then 5 minutes locked | Q2, Q3 | DEPLOYED (`0102`, `0103`, sign-in function; `dd317bf`, `0da236e`) |
| 9 | Whole-branch checks, review, owner hand checks | | DONE bar owner hand checks (`0104`, `0105`, `0dd2be7`) |
| 10 | Small leftovers from the final review | Q10, Q11 | DEPLOYED (`0106`, `sign-in` redeployed; `82ca78f`, `0a488f4`) |
| 11 | Discount codes are checked as they're typed: works, or why not | Q12, Q13 | DEPLOYED (`0107`, `079a01f`, `ae14404`) |

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
| Q10 | **Second round (owner, 2026-11-19): the final review's small leftovers go in.** A Day pass booking must also **end** by the pass's midnight (11 pm to 1 am is refused with `outside_membership`); other memberships keep the start-only rule. A Day pass paid just after midnight lasts through that new day: kept, it's in the member's favour and the popup already says "until midnight". | Claude's call |
| Q11 | Leftovers fixed as found: the sign-in function answers a network failure with `auth_unavailable` (`error.status || 503`); the member's Payments page says **Refunded** when a refund for that payment was repaid (the member's own `refund_requests` rows); its Cancel dialog keeps My membership's "you already sent proof" warning; the Cashier and `ItemsAsync` list only **published** packages (no draft Day pass price); the demo's Transactions filter follows a refund row to its payment, as the database does; two doc comments go back above their own members. | Claude's call |
| Q12 | **Discount codes checked as they're typed (owner, 2026-11-19).** In the Buy popup (Packages) and on Book an amenity, a code is checked about half a second after the member stops typing, and when the box loses focus. Under the box, one line: **"Code works: 10% off. You pay ₱1,350.00."** or the reason it doesn't: **"There's no code with that name."**, "You've already used this code.", "This code has been used up.", "This code has expired.", "That code doesn't work for this package." Pay is unchanged: the server checks the code again when the payment is made. An empty box shows nothing. Recommended: both places, since a booking takes codes the same way. | Owner, 2026-11-19: both places |
| Q13 | Checking a code tells whether it exists, which Pay already did. To stop someone guessing codes quickly, a member can check at most **20 codes in 10 minutes**; after that the line says "Too many checks. Wait a few minutes." Recommended. | Owner, 2026-11-19: 20 in 10 minutes |
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

- [x] Reproduce on the real project (demo build won't show it): change password to the same password; note the raw body.
- [x] Failing tests: `ErrorText.For(Validation, "{\"code\":422,\"error_code\":\"same_password\",\"msg\":\"New password should be different from the old password.\"}")` → "Pick a password that's different from your old one."; same for `weak_password`; `ErrorText.For(Validation)` doesn't contain "membership".
- [x] Fix, tests pass, `ponytail:ponytail-review`, commit.

## Phase 2: Who may book (Q1)

### Task 2: Close the hole

**Files:** found by reproduction; likely `book_amenity_for` (latest body: 0090 + 0093 patch) or the app's Book an amenity path; `src/GymMembership.App/MemberShell.xaml(.cs)` and `PhoneNavigation.cs` (Book an amenity becomes `members-only`), `supabase/tests/local/access.mjs`, test `tests/GymMembership.Tests/AccessTests.cs`.

- [x] Reproduce the owner's booking first (systematic-debugging): which account, which path (pass, coach, paid, down payment).
- [x] Failing tests: an account with no membership → `no_active_membership` for booking and for a time request; the desk's booking for the same member still works.
- [x] Fix at the root (one guard in the shared function, not per caller), migration if the database needs it, push (dry run first), live grants, screens checked, commit.

## Phase 3: Day pass package (Q1, Q9)

### Task 3: The package and its term

**Files:** next migration `00xx_day_pass_package.sql` (`is_day_pass` column and partial unique index on sale; activation sets `ends_at` to the next Manila midnight for a Day pass; `avail_membership` refuses a Day pass with `already_member` when `is_active_member`; `extend_membership` refuses a Day pass; the walk-in price reads the Day pass package), `supabase/tests/local/day-pass.mjs`, `Models.cs` (`MembershipPackage.IsDayPass`), `Foundation.cs` (`already_member`: "You already have a membership, so you don't need a Day pass."), demo mirror, test `tests/GymMembership.Tests/DayPassPackageTests.cs`.

- [x] Failing tests: bought and verified at 9 am → active until midnight Manila, can book an amenity and send a time request; at 12:01 am → not active; member with a membership → `already_member`; two Day pass packages on sale → refused.
- [x] Migration, push, grants, commit.

### Task 4: Admin, member and desk screens

**Files:** Packages admin page and view model ("Day pass" switch), `PackagesPage.xaml` (Day pass shows "Today only, ends at midnight"), `CashierViewModel` (`Kind == "Day pass"` with a `Member` sells the Day pass package; without one stays `walk_in`; price from the package), tests `DayPassNameTests.cs` updated, `CashierDayPassTests.cs`.

- [x] Tests first, then screens (`impeccable`): Packages on Android and Windows, light/dark, 1.3 font scale; Cashier and admin Windows only. Commit.

## Phase 4: Online only for members (Q6)

### Task 5: Database refuses member cash

**Files:** next migration `00xx_members_online_only.sql` (`check_package_method` and `book_amenity`: `cash` from a member → `online_only`), `supabase/tests/local/online-only.mjs` (also: a desk cash payment's audit row names the staff actor), `Foundation.cs` (`online_only`: "Pay online here, or pay cash at the front desk and they'll record it."), demo mirror.

- [x] Failing tests, migration, push, grants, commit.

### Task 6: Screens

**Files:** `PackagesViewModel` / `PackagesPage.xaml` (Buy popup loses step 2; `Method` always online), Book an amenity's `PayOption` (cash gone), `MyMembershipViewModel` notices, tests `BuyPopupTests.cs`, `CashPackageTests.cs` updated.

- [x] Tests first, then screens (`impeccable`), Android and Windows, light/dark, 1.3 font scale, commit.

## Phase 5: Remove Void (Q5)

### Task 7: Void out, statuses in plain words

**Files:** delete `VoidPaymentsPage.xaml(.cs)` and `VoidPaymentsViewModel`; `RefundsPage.xaml(.cs)` (no Part switch), DI registration, next migration `00xx_drop_void_payment.sql` (`drop function public.void_payment(bigint, text)`), demo `void_payment` case removed, one status label helper reused by Payments (Phase 7) and Transactions (Phase 6): `PaymentStatusText(Payment) -> string`, test `tests/GymMembership.Tests/PaymentStatusTests.cs`.

- [x] Failing test for the labels (Pending, Paid, Failed, Refunded, Voided), remove, build, tests, push, commit.

## Phase 6: Transactions filters (Q4)

### Task 8: Two filters, one per tab

**Files:** `AuditViewModel` (`_auditFilter`, `_moneyFilter`; money fields: `What`, `PaidWith`, `Status`, `Customer`, `RecordedBy`, dates), `AuditFilter` in `Services.cs` (money fields), `audit_log_page` + export (next migration: filter by those fields when `p_money`; Day pass rows = `walk_in` payments and Day pass package payments), `AuditPage.xaml` (filter row per tab), test `tests/GymMembership.Tests/TransactionsLogTests.cs` (Review Focus 5).

- [x] Failing tests, migration and push, screen (`impeccable`, Windows only), commit.

## Phase 7: Payments page (Q8)

### Task 9: My payments

**Files:** `PaymentsViewModel` in `MemberViewModels.cs` (`Kind` filter All / Membership / Amenity, Day pass under Membership; `Page`, 10 a page, own rows only), `src/GymMembership.App/Views/PaymentsPage.xaml(.cs)`, `MemberShell.xaml`, `PhoneNavigation.cs`, `IconRail.Windows.cs` (icon), `MyMembershipPage.xaml` (payment list out, waiting payment card stays), test `tests/GymMembership.Tests/MyPaymentsTests.cs` (another member's payment never shows; filter works; a Day pass shows under Membership).

- [x] Check the `payments` select policy only returns own rows for members (database test).
- [x] Tests first, page with `impeccable`: phone two-line rows (what + amount, date + status chip), Windows table; light/dark, 1.3 font scale. Commit.

## Phase 8: Rate limits (Q2, Q3)

### Task 10: Package buys

**Files:** next migration `00xx_package_buy_limit.sql` (inside `avail_membership` / `extend_membership`: count the caller's package payments created in the last hour; 10 or more → `too_many_tries` with minutes left), `supabase/tests/local/buy-limit.mjs`, `Foundation.cs` sentence, demo mirror.

- [x] Failing test (11th refused, after the hour allowed), migration, push, commit.

### Task 11: Sign-in lock (per Q2)

**Files (a):** next migration `00xx_sign_in_lock.sql` (`login_attempts(user_id, wrong, locked_until)`, hook function `password_verification_hook(event jsonb) returns jsonb`: reject while locked, count wrong, lock 5 min at 10, reset on success), enable the hook in the Supabase dashboard (owner step) and `supabase/config.toml`. **(b):** `supabase/functions/sign-in/index.ts` + the same table, `SupabaseGateway.SignInAsync` and `ChangePasswordAsync` call it. Both: `Foundation.cs` (`account_locked`: "Too many wrong tries. Try again in N minutes."), demo mirror, tests `supabase/tests/local/sign-in-lock.mjs`, `tests/GymMembership.Tests/SignInLockTests.cs` (Review Focus 2, 3).

- [x] Failing tests, build, push, owner enables the hook (a), live check with a test account, commit.

## Phase 10: Small leftovers (Q10, Q11)

### Task 12: Day pass bookings end by midnight (next migration)

**Files:** next migration `01xx_day_pass_booking_ends.sql` (`book_amenity_for`, the 0098 term check: when that term's package is the Day pass, also `ends_at >= v_end`), `supabase/tests/local/day-pass-package.mjs`, demo `DemoReservations.cs`, test `DayPassPackageTests.cs`.

- [x] Failing tests: a Day pass holder booking 11 pm to 1 am → `outside_membership`; 9 to 10 pm → booked; a Monthly member's booking that runs past their term's end still works. Migration (`0106`), push, grants, commit (`82ca78f`).

### Task 13: The other leftovers

**Files:** `supabase/functions/sign-in/index.ts` (`error.status || 503`, redeploy); `Services.cs` (`PaymentItem` gets `Refunded`, `MyPaymentsAsync` reads the member's paid `refund_requests`; `PaymentStatus.Text(status, refunded)` back; `ItemsAsync` membership list `PublishedAt is not null`); `PaymentsPage.xaml.cs` (HasProof warning); `DemoArchive.cs` (`MoneyFilter` follows `refund_requests` to the payment; doc comment above `AuditRecords`); `DemoGateway.cs` (doc comments above `OwnerSetActive`, `OwnerVoidAsync`, `OwnerMakeCash`); tests `MyPaymentsTests.cs`, `CashierTests.cs`, `TransactionsLogTests.cs`.

- [x] One failing test per behaviour (Refunded on Payments, a draft package not sold at the Cashier, demo refund rows under Status = Refunded), fix, whole suite, commit (`0a488f4`).

## Phase 11: Codes checked as they're typed (Q12, Q13)

### Task 14: `check_discount_code` (next migration)

**Files:** next migration `01xx_check_discount_code.sql`: `check_discount_code(p_code text, p_kind text, p_item bigint) returns table(percent int, amount_off numeric, you_pay numeric)` for the signed-in member. `p_kind` is `package` or `amenity`. It reuses `apply_discount` (exists, used, limit, expiry) and the item check `guard_payment_discount_item` uses (0060), writing nothing; it raises the same codes Pay raises (`invalid_code`, `code_already_used`, `code_limit_reached`, `code_not_for_item`). Q13's limit: a small `code_checks(member_id, checked_at)` log, 20 in 10 minutes, then `too_many_checks`. Granted to authenticated only. `supabase/tests/local/discount-check.mjs`.

- [x] Failing tests: a good code returns its percent and the price after; each refusal gives its code; a code for amenities only refused for a package; the 21st check in 10 minutes refused; nothing is written to `payments` (uses are counted from payments); anon can't call it. Migration (`0107`), push, grants, commit (`079a01f`).

### Task 15: The line under the code box

**Files:** `MembershipService` (`CheckCodeAsync(string code, string kind, int itemId) -> AppResult<CodeCheck>`), demo mirror (`check_discount_code` case reusing the demo's discount rules), `PackagesViewModel` (`CodeCheck`, `CodeCheckText`, `CodeWorks`; checks on `DiscountCode` change after 500 ms with the newest check winning, and on unfocus; cleared on open and close), `BookAmenityViewModel` (same, for `Code`; the price split uses the checked percent, not only the listed codes), `PackagesPage.xaml` and `BookAmenityPage.xaml` (one line under the box: green check with the price for a good code, the reason in the error colour otherwise), `Foundation.cs` (the sentences in Q12, `too_many_checks`), tests `CodeCheckTests.cs`.

- [x] Tests first: typing a good code shows "Code works: …" with the price; a wrong one shows "There's no code with that name."; clearing the box clears the line; a slow answer for an old code never overwrites the newer one; closing the popup clears it.
- [x] Screens with `impeccable`: Android and Windows, light and dark, 1.3 font scale; the line must not push Buy off a small phone screen. Commit (`ae14404`).

## Phase 12: Live test findings (2026-11-20)

Found by driving the app's Core services against the live project as admin, coach, front desk and new test members (about 440 checks). Next migration `0109`.

| # | Bug | Where | Seen live |
|---|-----|-------|-----------|
| B1 | **Admin can't add an amenity.** Save fails with `permission denied for table amenities`. | `0014` grants INSERT on six named columns only; `0064` added `coach_fee` with `grant update (coach_fee)` but no INSERT grant. The `Amenity` model sends `coach_fee` on insert (`Models.cs:162`). Broken since 0064. | `SaveAmenityAsync(new Amenity{...})` as the super admin → 42501 |
| B2 | **A coach can mark attendance for a session that hasn't happened.** The session becomes `completed` / `no_show`, can no longer be cancelled (`already_processed`), and the member is alerted "Attendance recorded". | `record_session_attendance` (`0005`) has no time check; `AttendanceCalendar.cs:10` `CanMark => !Cancelled`. | Session tomorrow 11:00 marked `present` today |
| B3 | **A 100% code on an online purchase can't be paid.** It makes a ₱0 `qr` payment; `xendit-checkout` sends `amount: 0`, Xendit refuses, and the app shows "unavailable". The booking/payment waits until it expires and holds the code's use. | `book_amenity_for` / `avail_membership` / `extend_membership` (paid path) + `supabase/functions/xendit-checkout/index.ts` | Payment 73 (Boxing ring, code 100%) |
| B4 | **A member can be sent to pay for a slot that's already booked.** The paid path of `book_amenity_for` has no check for a confirmed booking on that slot (the walk-in function has one, `0092`), so the member pays online and the confirm then fails (refund). Same member double-booked too. | `book_amenity_for` paid path (`0090`, patched by `0093`, `0099`, `0106`) | Reservations 19 (confirmed, pass) and 20 (awaiting payment), same 10:00 slot |

Settings and design changes found by the same test:

| # | Change | Issue |
|---|--------|-------|
| C1 | **"Daypass" package is not a Day pass** | Live package id 8 (₱60, 1 day) has `is_day_pass = false`, so it is an ordinary 1-day membership: it runs 24 hours instead of ending at midnight Manila, members see it on Packages, the desk's Day pass ignores it and charges `gym_settings.walk_in_price`, and the Day pass rules (0098, 0106, `8bd7be3`) don't apply. |
| C2 | **Gym name** | `gym_settings.gym_name` is still "Gym Membership (demo)" after the Slim and Shape rebrand (`1fab6f3`); screens that read the name from settings show "(demo)". |
| C3 | **Each booking blocks the next hour** | The 30-minute cooldown (`plus_cooldown`, `0029`) dates from half-hour slots. With on-the-hour starts (`0093`), an 18:00–19:00 booking also blocks 19:00, so the next start is 20:00 and an amenity can be booked at most every other hour. The grid shows it correctly; it is lost capacity. |
| C4 | **Coach and Karl must change their password** | Both accounts have the must-change-password flag from their temporary passwords; the app asks for a new one at next sign-in. Expected behaviour, nothing to build. |

Decisions (recommended; owner to confirm before building):

- **Q14 (B3), recommended (a):** a payment that comes to ₱0 after a code is verified straight away with no Xendit step, for packages, extensions and bookings bought online. The member gets what the code promised; (b) would make a 100% code useless in the app.
- **Q15 (B4), recommended:** refuse a slot already taken (confirmed booking plus cooldown, or the member's own confirmed booking) before any payment is made, as the walk-in does (`0092`).
- **Q16 (C1), recommended:** new version of "Daypass" with the Day pass box ticked, published; the old one is archived automatically. Price stays ₱60.
- **Q17 (C2), recommended:** gym name "Slim and Shape".
- **Q18 (C3), recommended:** no cooldown between bookings (`plus_cooldown` returns the end time). Bookings are whole hours, so cleaning fits inside the booked hour. Alternative: keep 30 minutes but only for hours that are not on the hour (no effect today).

### Task 16: Amenity insert grant (B1)

**Files:** migration `0109_amenity_coach_fee_insert.sql` (`grant insert (coach_fee) on public.amenities to authenticated;`), `supabase/tests/local/catalog-lifecycle.mjs`.

- [x] Failing test: an admin inserts a draft amenity with a coach fee as `authenticated`. Migration (`0109`), push, check live grants, live check (create, delete draft test amenity), commit.

### Task 17: Attendance only once a session has started (B2)

**Files:** migration (`0110_attendance_started_only.sql`, `record_session_attendance`: `if v_s.scheduled_start > now() then fail('too_soon')`), `AttendanceCalendar.cs` (`CanMark` also needs the session started), demo mirror, tests.

- [x] Failing tests: marking a future session → `too_soon`, and the calendar hides the mark buttons; a session that has started still marks. Migration (`0110`), push, grants, commit.

### Task 18: ₱0 online payments (B3, Q14 (a))

**Files:** migrations (`0111_zero_amount_online_payments.sql`, `0114_free_online_payment_constraint.sql`: when the amount after the code is 0, the payment is made `verified` with a receipt and `activate_payment_target` / booking confirm runs at once), `supabase/functions/xendit-checkout/index.ts` (a ₱0 payment answers `already_processed`, never calls Xendit), app `OnlinePay` / `PackagesViewModel`, demo mirror, tests.

- [x] Failing tests: a 100% code on a package and a booking bought online → verified, term or booking active immediately, no Xendit call. Migrations (`0111`, `0114`), redeploy `xendit-checkout`, push, grants, commit.

### Task 19: Booked slot refused before paying (B4, Q15)

**Files:** migration (`0112_booked_slot_refused_before_pay.sql`, `book_amenity_for` paid path: confirmed bookings + cooldown, and member's own confirmed bookings), demo mirror.

- [x] Failing tests: paying for a slot someone has confirmed → `slot_taken` with no payment made; the member's own confirmed booking at that time → `member_busy`; a free slot still books. Migration (`0112`), push, grants, commit.

### Task 21: Day pass package and gym name (C1 Q16, C2 Q17; live data, no code)

- [x] Admin → Packages → "Daypass" → Create new version (`id=16`) → `IsDayPass = true` → Published (old `id=8` archived).
- [x] Admin → Gym settings → name `"Slim and Shape"` saved live.

### Task 22: No cooldown between bookings (C3, Q18)

**Files:** migration (`0113_no_booking_cooldown.sql`: `plus_cooldown` returns `p_end`; `reindex table public.reservations`), demo `DemoReservations.cs` / `DemoRequests.cs` (`Cooldown` zero), xUnit booking tests.

- [x] Failing tests: 18:00–19:00 booked → 19:00 books; overlapping hours still refused. Migration (`0113`), push, live check, commit.

### Task 20: Clean up the live test records

- [x] Archived 10 `@example.com` test accounts and cancelled test bookings/codes (`claude.desk.1120071128@example.com` and `claude.codes.1120071443@example.com` remain with `account_has_commitments`).

## Phase 9: Checks

- [x] Whole test suite and database tests green; live grants checked for every migration on this branch.
- [x] `superpowers:requesting-code-review` on the whole branch; fixes committed.
- [ ] Owner hand checks: each finding above on the real project, Android and Windows.
- [x] Branch stays off `main`.

## Session log

- 2026-11-19: plan written, branch `owner-fixes-2026-11-19` made.
- 2026-11-19: owner chose Q1 = (b) (Day pass as a package, desk too) and Day pass under Membership (Q8); Phase 3 added. Owner: Q2 = (a), Q3 = 1 hour, build it natively.
- 2026-11-19: built Phases 1-8 natively; rulings in `.superpowers/sdd/2026-11-19-owner-fixes/progress.md`. Q2 (a) needs the Team plan (Supabase gates the Password Verification hook), so the sign-in Edge Function (b) was built.
- 2026-11-19: owner asked for the leftovers in the plan and for codes checked as they're typed: Phases 10 and 11 added (Q10-Q13). Waiting on Q12, Q13.
- 2026-11-19: owner confirmed Q12 = both places, Q13 = 20 in 10 minutes. Built and deployed Phase 10 (`0106`, `sign-in` redeployed; `82ca78f`, `0a488f4`) and Phase 11 (`0107`, `079a01f`, `ae14404`). Pushed to `origin/owner-fixes-2026-11-19`.
- 2026-11-19/20 follow-ups (local commits, not pushed yet):
  - **Cashier live code check (`0108`, `183d696`):** Added `check_discount_code_for` RPC (pushed live) and live discount code feedback + instant Total update in `CashierViewModel` / `CashierPage.xaml`.
  - **Cashier Day pass for members without membership (`8bd7be3`):** Enabled discount code box, live check, and discount submission on the Cashier's **Day pass** tab when a published Day pass package exists and a member with no active membership is picked.
  - **My membership coaching cards (`bd84ac4`):** Added **Hired coach** and **Current session** cards to `MyMembershipPage.xaml` / `MyMembershipViewModel`.
  - **Settings password strength (`fa81050`):** Reused the `c:PasswordStrength` bar and checklist under **New password** in `SettingsPage.xaml` (for all accounts).
  - **Coach calendar on My shifts & Dashboard (`5a3d1d9`, `da1aa00`, `0576c33`, `befaf0e`):** Extracted `CoachCalendarView` so **My shifts** shows the Dashboard Month/Week calendar (with **Upcoming shifts** under the calendar and **Post a shift** under the chosen-day panel); replaced the Dashboard's **Post a shift** form with **Assigned amenities**.
  - **Amenity assignment wording (`bd854c4`):** Changed `"I run this"` / `"Only you run this"` / `"Run by …"` to `"Can be assigned in this amenity"` / `"Only you are assigned"` / `"Coaches: …"`.
  - **Removed Payments to verify (`0d694bf`):** Deleted `VerifyPaymentsPage` / `VerifyPaymentsViewModel`, removed `verify` from `StaffShell` and the front-desk `HomePage` (now 3 tiles + Coaches in the gym now).
  - **Member Payments payment-method dropdown (`d2a354b`, `9060ffa`):** Added `PaidWiths` (`All`, `Cash`, `Online`) dropdown (`Picker`) in `PaymentsPage.xaml` / `MyPaymentsViewModel` alongside the All / Membership / Amenity chips.
  - **Compact stacked layouts on narrower Windows windows (`0e6fbe3`):** Removed `StackedSideWidth` from `SideLayout` on `MyMembershipPage.xaml`, `RequestsPage.xaml`, `CoachCalendarView.xaml`, `SessionsPage.xaml`, and `CashierPage.xaml`, and removed the 2-column `CardGrid` wrapper around the single settings panel in `GymSettingsPage.xaml` so stacked panels fill the full width without an empty right column.
  - **Refunds page filters (`3e677a4`):** Added status filter chips (`All`, `To review`, `To repay`, `Repaid`, `Rejected`), an item type dropdown (`All items`, `Membership`, `Day pass`, `Amenity`), and a member/item search box in `RefundsPage.xaml` / `RefundsViewModel`.
  - **Rebrand Gym Center to Slim and Shape (`1fab6f3`):** Updated `"Gym Center"` / `"Health & Fitness Gym Center"` to `"Slim and Shape"` / `"Health & Fitness Slim and Shape"` across app titles, shells, `AuthLayout`, `IconRail.Windows.cs`, `DemoGateway.cs`, `LogoTests.cs`, and `gym_badge.png`.
  - **Open item noted:** Desk Xendit payments log two rows in Transactions (`Desk payment sent to Xendit` + `Paid online (Xendit)`); waiting on owner choice (hide sent-to-Xendit from Transactions, label Pending, or leave as-is).
- 2026-11-20: live test of all roles against the real project (~440 checks). Four bugs found (B1-B4) and four settings/design changes (C1-C4) listed as Phase 12, with recommended decisions Q14-Q18 waiting on the owner's OK. Online payment completion not tested (needs a Xendit test-mode payment by hand).
