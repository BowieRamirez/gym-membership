# Full-Page Layout Plan

> **Status (2026-10-31): Phases 1-3 built** (commits 6ea916c, 14581bb). Phase 4 waits for the owner; Phase 5 partly done (see below). Approved mockup: `docs/mockups/compact-pages.html` (owner: "Yes, build it"). Before/after reference: `docs/mockups/page-scan.html` (every page, every role, before this work). Branch `visual-redesign-2026-10-30`.

**Goal:** Every Windows page fills the window like the reference dashboard: no 1240 px centred column, a toolbar row, a strip of small number cards where the page has numbers, tables (header row, hairline rows) instead of half-empty card grids, and a 340 px side panel on the right for the page's form or selected item. Same palette and type. Phones keep their one-column layouts.

**Rules**
- Restyle and re-layout first. Anything that needs new data or new behaviour (filters, new numbers, front desk home lists) is Phase 4, each item small and tested in Core.
- Phones: a page that switches a list to a table keeps its card template on phones (`OnIdiom` picks the template). `SideLayout` stacks below 1100 px, so narrow windows and phones get one column with the form first, as today.
- AutomationIds the UI scripts use stay on the same controls.

## Phase 1: shared pieces
- [x] `PageWidth` uncapped; page padding 16.
- [x] `SideLayout` (Controls.cs): main + 340 px side panel at 1100 px and wider, stacked (side first) below. One check in `PageLayoutTests`.
- [x] Styles: `SidePanel`, `Kpi` + `KpiValue`, `TablePanel`, `TableHead`, `TableRow`, `Hairline`, `Toolbar`.
- [x] Card grids allow up to 4 columns (they already drop columns below 320 px each).

## Phase 2: the six mockup pages
- [x] Member: My membership (number strip, terms + payments as tables, QR and pay-now in the side panel).
- [x] Member/coach: Time requests (requests table, compose form in the side panel).
- [x] Coach: Dashboard (number strip, calendar fills the page, selected day + post a shift in the side panel).
- [x] Coach/member: Sessions (table, reschedule form in the side panel).
- [x] Front desk: Home: its four tiles already form the number strip; the lists that fill the rest of the page are Phase 4.
- [x] Admin: Void a payment (table, void form in the side panel).

## Phase 3: every other page, same rules
- [x] Full width and up to 4 card columns everywhere (Packages, Amenities, Coaches, Trainees, Payments to verify, Amenity status, Packages and amenities, Discounts, Cancellations & refunds); Settings 3 columns; amenity photos 120 px on desktop.
- [x] My shifts: upcoming shifts on the left, hiring toggle + post a shift on the right.
- [x] Cashier: choices and today's cash on the left, the order on the right (below on phones).
- [x] Check-in: members on the left, scan + coaches on the right (fixed 340 px grid column; phones keep scan, coaches, members).
- [x] Admin overview: full width, 4 columns from 1400 px.
- [ ] Left as they were (already full width and dense enough): Book an amenity, Messages, Users and roles, Gym settings, Revenue, Logs & Archive, My coach profile, Verify amenity usages.

## Phase 4: new behaviour from the mockup (each one in Core with a test)

**Owner, 2026-10-31:** "update the plan first about the extras … I'll continue it later". Not started. Each item: the logic in a Core view model with an xUnit test in `tests/GymMembership.Tests`, then the XAML. Filters are client-side over lists the page already loads, so they need no new queries. Check each in demo mode as Phases 2-3 were (see "How to check" below).

**4a. Status filters (segmented buttons in a toolbar row above the table)**
- [ ] Sessions (`SessionsViewModel`, `ViewModels/StaffViewModels.cs`): All / Scheduled / Completed / Cancelled or no show. Keep the full list, expose a filtered `Sessions` view; the choice survives a reload. Same page for members and coaches.
- [ ] My membership payments (`MyMembershipViewModel`, `ViewModels/SubscriptionViewModels.cs` + `MemberViewModels.cs`): All / Verified / Pending / Expired over `Payments` (`PaymentItem.Status`).
- [ ] Optional, coach Sessions search by member name (mockup shows it); only if the list gets long.
- Buttons: `SmallOutlineButton`, the selected one `LimeSmallButton` via `DataTrigger`, as Month/Week on the Dashboard.

**4b. Front desk Home lists (`HomeViewModel`, `IDeskHomeService`)**
- [ ] "Payments to verify" table under the tiles: payer, item, code, amount, Verify button that opens Payments to verify (reuse `IPaymentStaffService`, the source `VerifyPaymentsViewModel` uses; do not verify from Home, verifying needs the proof screenshot).
- [ ] "In the gym now" table: coaches checked in (`CoachPresenceRow` via `ICoachService`, as `CheckInViewModel` loads them) with Check out.
- [ ] Side panel: member search + Check in (same calls as Check-in) and amenity status list. If this duplicates Check-in too much, keep only the two tables.
- Layout: tiles stay the number strip; the two tables side by side (`CardGrid Columns=2`), side panel via `SideLayout`.

**4c. Extra numbers (Kpi cards, `Kpi` + `KpiValue` styles)**
- [ ] My membership: "Ends" (end of the current term, from `Terms`) and "Payments waiting" (count of pending/unpaid `Payments`), beside the lime days-left card.
- [ ] Time requests: "Waiting for a reply" (count of `Requests`), "Next session" (soonest scheduled session; needs `ISessionService`, which `SessionsViewModel` uses).
- [ ] Coach Dashboard: a fourth card, "Trainees" (count; `TraineesViewModel`'s source).
- Every number also says what it is in words (no colour-only meaning), like the existing tiles.

**How to check (demo mode)**
- Real config `src/GymMembership.App/Resources/Raw/supabase.json` must be moved out of the project before a demo build and put back right after (never commit it, never leave a `.bak` in the repo). The build output only lacks it while built that way; rebuild normally afterwards.
- Demo sign-in buttons on the login page: Member, Coach, Front desk, Admin, Sub-admin.
- Close any running `GymMembership.App.exe` first or the build can't replace it (ask the owner if it isn't yours).

**Pitfalls found in Phases 1-3**
- A style `BasedOn` a key defined lower in `Styles.xaml` builds fine but crashes the app at start-up (`PageLayoutTests.Styles_are_based_only_on_styles_defined_above_them`).
- Table rows: give the button column a fixed width; `Auto` sizes per row and the columns stop lining up with the header.
- `SideLayout` takes exactly two children (main, side); wrap several side panels in one `VerticalStackLayout`. A `CollectionView` needs a bounded height, so pages built on one (Check-in) use a grid with `ColumnDefinitions="{OnIdiom Phone='*,0', Default='*,340'}"` instead.
- Desktop-only hints in a side panel: wrap them in `<ContentView IsVisible="{OnIdiom Phone=False, Default=True}">` so phones don't show them above the list.

## Phase 5: check
- [x] xUnit 765/765, Windows and Android builds. - [ ] `scripts/Test-WindowsAdminNavigation.ps1` not run yet.
- [x] Demo-mode checks at 1920 px: My membership, Sessions (member and coach), Time requests (member and coach), Dashboard, Void a payment, Cashier, Check-in, My shifts, Admin overview.
- [ ] Not yet: every other page at 1920 px, any page at 1100 px, the Android emulator.
