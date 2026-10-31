# Full-Page Layout Plan

> **Status (2026-10-31): Phases 1-3 built** (commits 6ea916c and the next). Phase 4 waits for the owner; Phase 5 partly done (see below). Approved mockup: `docs/mockups/compact-pages.html` (owner: "Yes, build it"). Before/after reference: `docs/mockups/page-scan.html` (every page, every role, before this work). Branch `visual-redesign-2026-10-30`.

**Goal:** Every Windows page fills the window like the reference dashboard: no 1240 px centred column, a toolbar row, a strip of small number cards where the page has numbers, tables (header row, hairline rows) instead of half-empty card grids, and a 340 px side panel on the right for the page's form or selected item. Same palette and type. Phones keep their one-column layouts.

**Rules**
- Restyle and re-layout first. Anything that needs new data or new behaviour (filters, new numbers, front desk home lists) is Phase 4, each item small and tested in Core.
- Phones: a page that switches a list to a table keeps its card template on phones (`OnIdiom` picks the template). `SideLayout` stacks below 1100 px, so narrow windows and phones get one column with the form first, as today.
- AutomationIds the UI scripts use stay on the same controls.

## Phase 1: shared pieces
- [x] `PageWidth` uncapped; page padding 16.
- [x] `SideLayout` (Controls.cs): main + 340 px side panel at 1100 px and wider, stacked (side first) below. One check in `PageLayoutTests`.
- [x] Styles: `SidePanel`, `Kpi` + `KpiLabel` + `KpiValue`, `TableHead`, `TableRow`, `Hairline`, `Toolbar`.
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
- [ ] Status filter on Sessions; status filter on My membership payments.
- [ ] Front desk Home: payments to verify and "in the gym now" lists.
- [ ] Extra numbers: membership end date, payments waiting; requests waiting, next session.

## Phase 5: check
- [x] xUnit 765/765, Windows and Android builds. - [ ] `scripts/Test-WindowsAdminNavigation.ps1` not run yet.
- [x] Demo-mode checks at 1920 px: My membership, Sessions (member and coach), Time requests (member and coach), Dashboard, Void a payment, Cashier, Check-in, My shifts, Admin overview.
- [ ] Not yet: every other page at 1920 px, any page at 1100 px, the Android emulator.
