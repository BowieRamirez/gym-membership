# Full-Page Layout Plan

> **Status (2026-10-31): in progress.** Approved mockup: `docs/mockups/compact-pages.html` (owner: "Yes, build it"). Before/after reference: `docs/mockups/page-scan.html` (every page, every role, before this work). Branch `visual-redesign-2026-10-30`.

**Goal:** Every Windows page fills the window like the reference dashboard: no 1240 px centred column, a toolbar row, a strip of small number cards where the page has numbers, tables (header row, hairline rows) instead of half-empty card grids, and a 340 px side panel on the right for the page's form or selected item. Same palette and type. Phones keep their one-column layouts.

**Rules**
- Restyle and re-layout first. Anything that needs new data or new behaviour (filters, new numbers, front desk home lists) is Phase 4, each item small and tested in Core.
- Phones: a page that switches a list to a table keeps its card template on phones (`OnIdiom` picks the template). `SideLayout` stacks below 1100 px, so narrow windows and phones get one column with the form first, as today.
- AutomationIds the UI scripts use stay on the same controls.

## Phase 1: shared pieces
- [ ] `PageWidth` uncapped; page padding 16.
- [ ] `SideLayout` (Controls.cs): main + 340 px side panel at 1100 px and wider, stacked (side first) below. One check in `PageLayoutTests`.
- [ ] Styles: `SidePanel`, `Kpi` + `KpiLabel` + `KpiValue`, `TableHead`, `TableRow`, `Hairline`, `Toolbar`.
- [ ] Card grids allow up to 4 columns (they already drop columns below 320 px each).

## Phase 2: the six mockup pages
- [ ] Member: My membership (number strip, terms + payments as tables, QR and pay-now in the side panel).
- [ ] Member/coach: Time requests (requests table, compose form in the side panel).
- [ ] Coach: Dashboard (number strip, calendar fills the page, selected day + post a shift in the side panel).
- [ ] Coach/member: Sessions (table, reschedule form in the side panel).
- [ ] Front desk: Home (four tiles as the number strip).
- [ ] Admin: Void a payment (table, void form in the side panel).

## Phase 3: every other page, same rules
- [ ] Member: Packages, Amenities, Book an amenity, Coaches, Settings.
- [ ] Coach: Trainees, My shifts, My coach profile.
- [ ] Front desk: Payments to verify, Cashier, Check-in, Amenity status, Verify amenity usages.
- [ ] Admin: Overview, Users and roles, Packages and amenities, Discounts, Cancellations & refunds, Gym settings, Revenue, Logs & Archive.

## Phase 4: new behaviour from the mockup (each one in Core with a test)
- [ ] Status filter on Sessions; status filter on My membership payments.
- [ ] Front desk Home: payments to verify and "in the gym now" lists.
- [ ] Extra numbers: membership end date, payments waiting; requests waiting, next session.

## Phase 5: check
- [ ] xUnit, both builds, `scripts/Test-WindowsAdminNavigation.ps1`.
- [ ] Demo-mode screenshots of every page, all roles, Windows at 1920 and 1100; Android emulator sample.
