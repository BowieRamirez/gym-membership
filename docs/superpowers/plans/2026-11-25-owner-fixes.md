# Owner fixes 2026-11-25 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

Created: 2026-11-25
Last updated: 2026-11-25
Status: Fix 1 BUILT (app only, no migration). More fixes get added below as the owner sends them.
Branch: `owner-fixes-2026-11-25` (cut from `main` at `df3f54c`, v0.11.0). Owner merges to `main` when done; no push or PR without the owner.
Next migration: `0151`.

**Goal:** The owner's fixes after the v0.11.0 release.

**Tech Stack:** .NET MAUI (Core + App), CommunityToolkit.Mvvm, xUnit; Supabase when a fix needs the database.

## How to use this plan

- One fix at a time; each ends working, tested and committed.
- Skills: `ponytail` full mode; `superpowers:test-driven-development` for rules; `superpowers:systematic-debugging` for bugs; `impeccable` and `frontend-design` for screen changes (staff pages are Windows only); `ponytail:ponytail-review` on each diff; `superpowers:verification-before-completion` before "done".
- Push a fix's migrations live when it is finished (dry run first, then check live grants).
- The owner tests the app themself; unit tests and builds here.

| Fix | What | Status |
|---|---|---|
| 1 | Cashier: Membership is register-only; Renewal lists existing accounts; no Register on Day pass | BUILT |

## Notes from today (no change)

- **Why some bookings have no refund button at the desk (Amenity status):** the desk asks for refunds only for walk-ins (0096, `BookedRow.CanRequestRefund`). A member cancels their own booking in the app (`cancel_reservation`), which sends the refund request to the System admin. Working as decided.

## Fix 1: Cashier customer by kind

Owner, 2026-11-25 (screenshot of Cashier, Membership with the register form open): when people want an account made by the front desk, Membership should be just "Register new member", with no customer name search. Renewal is for people who already have an account, so it is the list of members, with no Register button. Day pass had the Register button again; remove it there too.

Decisions (asked 2026-11-25):

- Renewal lists every existing account: one with no active membership buys a package again (paid as a `membership`), one still active extends (paid as a `renewal`, as before). Someone on a Day pass until midnight is hidden. *Owner may narrow this to "no membership only"; then active members extend from their own app.*
- Renewal: the list shows at once, and the search box narrows it.
- Day pass: keep the member search; drop only Register.

Built:

- [x] `CashierViewModel.ShowsSearch` (not Membership) and `ShowsRegisterForm` (Membership with no member chosen). Register form has no Back; after registering, the new member is chosen and the packages show; "Change" brings the form back.
- [x] Removed `IsRegistering`, `StartRegister`, `CancelRegister` and the Register new member button.
- [x] `MemberSuggest.RunAsync(listAll)`: Renewal lists every member with nothing typed; the dropdown only shows for a typed search. Change customer on Renewal reloads the whole list.
- [x] `PayKindNow` replaces `KindKey`: Renewal pays as `renewal` for an active member, else `membership`. Used for items, cash, Xendit and the discount check. Renewal lists no items until a member is picked; a status re-read that flips active reloads them.
- [x] `Visible`/`IneligibleReason`: Renewal hides only an active Day pass ("already has a Day pass until midnight").
- [x] No-match text: "New here? Register them under Membership."
- [x] Tests: `Membership_registers_while_Renewal_and_Day_pass_search`, `Register_a_new_member_and_they_are_chosen`, `Renewal_lists_accounts_and_a_member_with_no_membership_buys_a_package`; Renewal filter test updated. 1245 pass; Windows app builds.
- [ ] Owner hand check on Windows: Membership, Renewal (lapsed and active member), Day pass.

Ponytail review: `ShowHits`, `ShowNoMatch` and `ShowSuggestions` are now plain aliases; can be folded into `HasHits`, `NoMatch` and `HasSuggestions` in a later cleanup.
