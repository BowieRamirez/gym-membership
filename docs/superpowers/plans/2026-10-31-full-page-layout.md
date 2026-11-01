# Full-Page Layout Plan

> **Status (2026-11-01): Phases 1-4 built**, plus narrow-window tables, a new Settings layout and a light-mode palette (see "After Phase 4"). Phases 1-3: commits 6ea916c, 14581bb. Phase 5 partly done (see below). Approved mockup: `docs/mockups/compact-pages.html` (owner: "Yes, build it"). Before/after reference: `docs/mockups/page-scan.html` (every page, every role, before this work). Branch `visual-redesign-2026-10-30`.

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

**Owner, 2026-10-31:** "update the plan first about the extras … I'll continue it later". **2026-11-01:** Phase 4 built: 4a filters, 4b front desk lists, 4c numbers. No database changes were needed. The optional coach Sessions search is still skipped. Each item: the logic in a Core view model with an xUnit test in `tests/GymMembership.Tests`, then the XAML. Filters are client-side over lists the page already loads, so they need no new queries. Check each in demo mode as Phases 2-3 were (see "How to check" below).

**4a. Status filters (segmented buttons in a toolbar row above the table)**
- [x] Sessions (`SessionsViewModel`, `ViewModels/StaffViewModels.cs`): All / Scheduled / Completed / Cancelled or no show. Keep the full list, expose a filtered `Sessions` view; the choice survives a reload. Same page for members and coaches. (2026-11-01, `StatusFilterTests`; the buttons seen in demo mode as coach, the filtering itself only tested in xUnit.)
- [x] My membership payments (`MyMembershipViewModel`, `ViewModels/SubscriptionViewModels.cs` + `MemberViewModels.cs`): All / Verified / Pending / Expired or rejected over `Payments` (`PaymentItem.Status`); voided payments show under All only. (2026-11-01, `StatusFilterTests`; not yet checked in demo mode.)
- Empty state under a filter says "No … with this status. Choose All …" instead of "No … yet".
- [ ] Optional, coach Sessions search by member name (mockup shows it); only if the list gets long.
- Buttons: `SmallOutlineButton`, the selected one `LimeSmallButton` via `DataTrigger`, as Month/Week on the Dashboard.

**4b. Front desk Home lists (`HomeViewModel`, `IDeskHomeService`)**
- [x] "Payments to verify" table under the tiles: payer, item, code, amount, Verify button that opens Payments to verify (reuse `IPaymentStaffService`, the source `VerifyPaymentsViewModel` uses; do not verify from Home, verifying needs the proof screenshot).
- [x] "In the gym now" table: coaches checked in (`CoachPresenceRow` via `ICoachService`, as `CheckInViewModel` loads them) with Check out.
- [x] Side panel: skipped. Member search and check-in are the Check-in page, and amenity status is already a tile, so only the two tables were built.
- (2026-11-01, `DeskHomeTests.Home_lists_payments_to_verify_and_coaches_in_the_gym`, xUnit 773/773. Both lists refresh with the 20-second tile refresh. Checked in demo mode at ~900 px and maximised: Verify opens Payments to verify, Check out asks first, then shows a notice.)
- Layout: tiles stay the number strip; the two tables side by side (`CardGrid Columns=2`), side panel via `SideLayout`.

**4c. Extra numbers (Kpi cards, `Kpi` + `KpiValue` styles)**
- [x] My membership: "Current term ends" (the active term's end, Manila, or "None") and "Payments waiting" (count of pending `Payments`), beside the lime days-left card in a 3-column `CardGrid`.
- [x] Time requests: "Waiting for a reply" (count of `Requests`), "Next session" (soonest of `RequestsView.ActiveSessions`, already loaded, so no new call). The table title below became "Open requests" so the words aren't repeated.
- [x] Coach Dashboard: a fourth card, "Trainees" (`ICoachService.TraineesAsync` count, active hires).
- (2026-11-01, `ExtraNumbersTests`, xUnit 771/771, Windows build. Dashboard Trainees card seen in demo mode; My membership and Time requests cards not yet.)
- Every number also says what it is in words (no colour-only meaning), like the existing tiles.

**How to check (demo mode)**
- Real config `src/GymMembership.App/Resources/Raw/supabase.json` must be moved out of the project before a demo build and put back right after (never commit it, never leave a `.bak` in the repo). The build output only lacks it while built that way; rebuild normally afterwards.
- Demo sign-in buttons on the login page: Member, Coach, Front desk, Admin, Sub-admin.
- Close any running `GymMembership.App.exe` first or the build can't replace it (ask the owner if it isn't yours).

**Pitfalls found in Phases 1-3**
- A style `BasedOn` a key defined lower in `Styles.xaml` builds fine but crashes the app at start-up (`PageLayoutTests.Styles_are_based_only_on_styles_defined_above_them`).
- Table rows: give the button column a fixed width; `Auto` sizes per row and the columns stop lining up with the header.
- `SideLayout` takes exactly two children (main, side); wrap several side panels in one `VerticalStackLayout`. A `CollectionView` needs a bounded height, so pages built on one (Check-in) use a grid with `ColumnDefinitions="{OnIdiom Phone='*,0', Default='*,340'}"` instead.
- Tables with wide fixed columns squeezed the text columns to a letter per line in narrow windows (owner screenshot, coach Sessions, 2026-11-01). Every table now uses `c:TableBody` (Controls.cs): row template + header from its `WideFrom` table width, phone cards below. Sessions/Requests 1300, payments 1150, Void 1000, terms 900. `PageLayoutTests.Tables_switch_to_cards_by_their_own_width` checks each `TableHead` has one.
- Settings (2026-11-01, owner: "looks basic with the card boxes with the same sizes"): lime account header (saved name, email, confirmed, Change picture), name + email in one panel, theme + password in the side column. Checked in demo mode at ~900 px and maximised.
- Row buttons inside a `DataTemplate`: use `Clicked` + `Row<TheViewModel>(sender, v => v.XCommand)` (BasePage), as every other page does. A `Command` bound through `RelativeSource AncestorType` builds but does nothing when clicked (found on Home Check out).
- Desktop-only hints in a side panel: wrap them in `<ContentView IsVisible="{OnIdiom Phone=False, Default=True}">` so phones don't show them above the list.

## After Phase 4: owner feedback, 2026-11-01
- [x] Narrow windows: tables switch to their cards (`c:TableBody`, see Pitfalls). Seen in demo mode on coach Sessions at ~900 px and maximised.
- [x] Settings re-laid out (see Pitfalls). Seen in demo mode.
- [x] Light mode, owner: "the left nav bar … and at the top of the pages … make the light mode off", "the blue color in the letters". Then the owner picked "#ECEFE8, slightly deeper sage" for the page (Colors.xaml, light only, dark unchanged):
  - Page, header, title bar and the area behind the rail: `ChalkLight` = `BackdropLight` = `Chalk` = #ECEFE8 (was a darker #DDE3DA frame round a #F2F4F0 page).
  - Cards: `CardLight` #FFFFFF with a 1 px `LineLight` edge (`Panel` `StrokeThickness` Light=1, Dark=0). Fields and rows inside cards: `SurfaceLight` #F2F4F0. Icon chips: `IconChipLight` #ECEFE8. The rail's desk-pages group: `ChalkLight`.
  - Rail: white with a 1 px `LineLight` edge in light mode (IconRail.Windows.cs).
  - Windows title bar: `Window.TitleBar` in App.xaml.cs with the backdrop colour and ink text, so the rail's backdrop no longer cuts into a blue system bar.
  - Links (`LinkButton`) and the focused-field border: ink in light mode, lime in dark. Blue (`PlateBlue`) now only means "scheduled".
  - Contrast tests pass on the new colours. Seen in demo light mode: sign-in, Admin overview, Users and roles.
- Owner kept the black "Open …" buttons on the Admin overview as they are.

## Android to match Windows, 2026-11-01
Owner: "match it with the windows version with the colors and the redesigns updated", for sign-in and the member and coach pages (the only Android users). The palette is shared, so the colours already carried over; these were the phone-only gaps, found and checked on the Pixel 7 emulator (API 36) in demo mode, light and dark:
- [x] Status bar: Material's `AppBarLayout` paints the theme's black `colorPrimary` behind it edge to edge; `PhoneNavigation.StyleBar` now gives it the Shell backdrop colour. `MainActivity` sets dark/light status and navigation icons from the app's theme (the saved one, not only the phone's).
- [x] Header account button: a drawn `chevron-down` chip on a bare 48 x 48 button (the font has no "⌄", Android showed a "v"); no longer cut off.
- [x] Tab labels: one 12sp size, bold when selected (`Platforms/Android/Resources/values/styles.xml`), so "Membership" fits.
- [x] More page (`PhoneMorePage`): white `Panel` cards with an icon chip, title and ›, palette colours instead of the old hard-coded ones.
- [x] My membership: the number strip moved above the main/side split (phones stacked the QR first). Phones: days left full width, the two small cards as a pair (`KpiValue`). On Windows the strip now spans the page width.
- [x] Time requests: its two number cards are a pair on phones too.
- [x] Coach Dashboard: number cards 2 x 2 on phones; `FlexLayout.Shrink="0"` on the session marks and the period row (Android's FlexLayout squeezed them to "Presen", "Lat" and hid the period title).
- [x] Android underline removed from `Picker`, `DatePicker`, `TimePicker` (as Entry/Editor already were).
- [x] Owner follow-up: Dashboard period row is a grid. Windows keeps one row; phones get Month | Week with Today on the right, then ‹ period › centred. Tab bar: the lime pill is 28 high and the items get 8 top / 4 bottom padding, so there is a gap above the tab name (`ActiveIndicatorLabelPadding` alone did nothing). Tab bar in dark mode, owner (after first keeping it light): "make the nav bar at android dark but not too dark to see the separation", and it flashed while switching tabs. Now `SurfaceDark` #2C302B (a step above the #161816 page), light names, muted icons, black icon on the lime pill; the Shell colour and the restyled bar are the same, so nothing flashes.
- [x] Settings: the Sign out button and `SettingsViewModel.SignOutCommand` removed (owner); Sign out stays in the header's account menu on Android.
- Left as they are, on purpose: filter buttons wrap to a second row (a sideways scroll would hide options); the sign-in brand card stays a fixed header (from the shared `AuthShell`).
- Found, not changed: Back on a main tab returns to the sign-in page without signing out, and Back from a More page lands on the first tab instead of the More list. Navigation, not this redesign.
- Pitfall: an incremental Android build kept `supabase.json` in the APK after it was moved out. Demo Android builds need `--no-incremental`, and check the APK has only `supabase.sample.json` before signing in.

## Phase 5: check
- [x] xUnit 773/773 (2026-11-01), Windows build. Android not rebuilt since Phase 3. - [ ] `scripts/Test-WindowsAdminNavigation.ps1` not run yet.
- [x] Demo-mode checks at 1920 px: My membership, Sessions (member and coach), Time requests (member and coach), Dashboard, Void a payment, Cashier, Check-in, My shifts, Admin overview.
- [ ] Not yet: every other page at 1920 px, any page at 1100 px, the Android emulator.
