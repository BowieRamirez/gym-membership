# Coach access add-on (Q1) — design

Created: 2026-11-21. Branch `desk-customer-shifts-2026-11-20`. Answers Q1 of `plans/2026-11-20-desk-customer-shifts.md`.

## Why

The owner sells coaching as its own packages ("Monthly (with coach included)", "Daypass (With coach included)"). A member who already bought a plain package (Monthly, 3 Months) has no way to get coaching until that term ends. The owner wants a **coach add-on on the Packages page** that a member with a membership can buy to book coaches.

## Approaches

| | Approach | Trade-off |
|---|---|---|
| A | Pay per session (bring back the per-hour add-on already in 0120) | Least work; members pay every booking, nothing to "buy" on Packages |
| **B** | **Coach access for the rest of the current term** (chosen by the owner, 2026-11-21) | Matches "buy add-on to access coaches" and how Coach-included packages work; new purchase kind |
| C | Upgrade the term to the Coach-included package, pay the difference | Swapping terms and money is the hardest and riskiest |

## Goals

- Members on a plain package see a **Coach access** card on Packages: price, "Book coaches until <term end>", **Buy add-on**.
- Once paid, they book coaches exactly like a Coach-included member: free per session, same limits (sessions a day, upcoming cap, no-show block).
- Admin sets the add-on price on each plain package.

## Non-goals

- No per-session paid coaching (approach A's path is switched off).
- No upgrade/swap of terms (C).

## Decisions (owner chose B; the rest are Claude's calls, marked)

1. **Price** — `membership_packages.coach_addon_price` (0119) now means "coach access for a full term of this package". The box comes back on Admin Packages for every plain package, **the plain Daypass included** (owner, 2026-11-21); hidden when Coach included is on (the existing check already forbids both). Copied on new version (0119 already does).
1a. **Day pass** (owner, 2026-11-21) — a member with an account who bought the plain Daypass (online or at the desk) can buy coach access for that day, e.g. ₱40 (the difference between the ₱60 Daypass and the ₱100 Daypass with coach). Scaling gives the full price for a 1-day pass. Sessions must end by Manila midnight (the booking already requires the session inside the term). The card is hidden when no coach has a free hour left that day (`free_coaches` for today is empty), so nobody pays for coaching they can't book. Today they can't get a coach at all: the plain Daypass offers none and a second Day pass is refused (`already_availed`).
2. **Scaled to days left** (Claude's pick, owner didn't object): pay `ceil(coach_addon_price × days_left / duration_days)` whole pesos, days counted in Manila days from today to the term end. The card shows the scaled price. Worked out by the server only.
3. **When it's offered** (Claude's call): member has an active term on a plain package with a price set, no coach access on that term yet, no add-on payment pending, and at least 1 day left (Day pass: a free coach hour left today, 1a). Otherwise no card.
4. **Buying** — new member function `buy_coach_access(p_code text default null)`: makes a `coach_addon` payment (online, Xendit, same `xendit-checkout` flow and expiry as packages) tied to the member's current term. On verify, `activate_payment_target` sets `membership_terms.coach_access_at`. Discount codes of kind `coach` work on it (the code applies to the scaled price).
5. **Booking** — `_book_coach_session_core` treats `includes_coach OR term.coach_access_at is not null` as coach included. Anything else → `coach_not_offered`. The per-session paid path (held sessions, 15-minute hold) is no longer reached; no live `coach_addon` payments exist (checked 2026-11-21), so nothing in flight.
6. **Renewal** — access ends with the term; the next term needs a new add-on (card shows again).
7. **Refunds** (Claude's call) — not refundable on its own; if the term is cancelled, access ends with it. Revisit if the owner wants add-on refunds.
8. **Desk** (Claude's call) — app only, online, like other member package payments (0099). Cashier sale can follow if asked.

## Pages (impeccable on each)

- **Packages** (member, Android + Windows, light/dark, 1.3 font scale): Coach access card with price, "until <date>", Buy add-on popup (code box with the live check, Pay online → Xendit, check on return).
- **My membership** (member): "Coach access until <date>" on the current term.
- **Admin Packages** (Windows): "Coach access price (full term)" box for plain packages.
- **Payments / Revenue / Transactions**: already label `coach_addon` "Coach add-on".

## Database

Migration `0127_coach_access_addon.sql`: `membership_terms.coach_access_at timestamptz` + `coach_access_payment_id`; `buy_coach_access`, a read function for the card's price (`coach_access_offer()`), `activate_payment_target` and the booking core updated; revoke from `public, anon, authenticated`, grant `authenticated`; demo mirror; tests in `supabase/tests/local/coach-access.mjs` and `tests/GymMembership.Tests`.

## Checks

- Plain Monthly member with 15 of 30 days left and price ₱400 → offered ₱200; pays; books a coach free; second session that day refused by the daily limit.
- Plain Daypass member, add-on ₱40, coach free at 3 pm → offered ₱40; pays; books 3-4 pm; a session ending after midnight refused.
- Plain Daypass member at 10:30 pm with no free coach hour left → no card; `buy_coach_access` refused.
- No price set, Coach-included package, access already bought, payment pending → not offered / refused.
- Unpaid add-on expires → no access.
- Renewed term → no access until bought again.
