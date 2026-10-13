# Member and Coach Mobile Layout Implementation Plan

Date: 2026-10-13
Status: Implemented on branch `mobile-layout` (builds and unit tests pass; not yet run on a device or emulator)

## Goal

Make all member and coach journeys usable on phones while preserving the existing desktop experience, ViewModels, commands, permissions, and backend rules. Use adaptive layouts within existing pages rather than separate mobile copies.

## Current foundations and risks

Inspection covered role shells, shared styles, and representative pages; a complete page audit remains the first implementation task.

- MemberShell and CoachShell already use a flyout on mobile and locked navigation on Windows.
- Existing card layouts, wrapping containers, and mostly 48-unit inputs provide a useful foundation.
- Fixed-width fields, side-by-side QR payment content, horizontal action rows, and tall inline forms are the main layout risks.
- SmallButton and SmallOutlineButton currently force a 40-unit height.
- MyMembershipPage contains a 168-unit QR image beside instructions and a large payment code.
- MemberShell rechecks membership access after navigation and rebuilds navigation when access changes; this behavior must remain intact.

## Scope

| Scope | Pages |
| --- | --- |
| Member-specific | My membership, Packages, Amenities, Coaches |
| Coach-specific | Trainees, My shifts, Attendance, My coach profile |
| Shared by both | Sessions, Time requests, Messages, Alerts, Settings |
| Supporting flows | Login, Register, guest-locked pages, dialogs, payment-proof selection |

Staff/admin redesign, bottom tabs, new dashboards, new business features, and database/backend changes are out of scope. Shared pages and resources must not regress staff usage.

## Phase 1 — Audit every affected screen

- [ ] Inspect each scoped page, its code-behind, bindings, navigation, and supporting dialogs.
- [ ] Record fixed widths/heights and layouts that can overflow.
- [ ] Identify primary and secondary actions.
- [ ] Record keyboard and scrolling behavior.
- [ ] Cover loading, empty, error, and disabled states.
- [ ] Identify shared usage by staff pages and resources.
- [ ] Produce a page-by-page implementation and verification checklist.

Follow the repository workflow in CLAUDE.md when implementation begins, including applicable design, planning, verification, and review guidance.

## Phase 2 — Establish a small adaptive-layout foundation

Use native MAUI layout controls and visual states first. Introduce shared helpers only where repeated patterns justify them.

### Proposed breakpoints

Measure available content width in device-independent units, not physical pixels or device labels. These are starting values to validate during the pilot.

| Available content width | Layout |
| --- | --- |
| Below 600 | Compact: single-column forms and cards; stacked actions |
| 600–899 | Medium: selective two-column fields where they fit |
| 900 and above | Expanded: existing desktop presentation with capped content widths |

### Rules

- [ ] Account for navigation space when determining available content width.
- [ ] Reflow on rotation and window resizing.
- [ ] Use approximately 16-unit compact page padding.
- [ ] Target at least 48-unit touch controls on Android.
- [ ] Allow text wrapping and system text scaling; avoid fixed heights that clip content.
- [ ] Preserve Barlow typography, status colors, light/dark themes, and the lane motif.
- [ ] Keep adaptive layout state in the UI layer, not business ViewModels.
- [ ] Introduce scoped mobile button variants or adaptive overrides rather than globally changing staff styles.
- [ ] Preserve drafts, selections, and current work when layout states change.

## Phase 3 — Keep navigation changes minimal

Retain the existing mobile flyout for the initial implementation.

- [ ] Verify every member and coach destination remains reachable.
- [ ] Clearly identify the current page.
- [ ] Preserve routes, navigation commands, and Android Back behavior.
- [ ] Preserve guest locks and membership access rechecks.
- [ ] Keep desktop navigation intact.

Bottom tabs are deferred. Restructuring Shell navigation alongside page adaptation adds avoidable routing and access-control risk.

## Phase 4 — Adapt member journeys

| Page | Planned mobile treatment |
| --- | --- |
| My membership | Retain the days-left hero. Stack QR, recipient details, payment code, reference field, and proof-upload action vertically. Reflow renewal/payment cards so amounts and buttons do not squeeze text. |
| Packages | Single-column package cards with readable prices and benefits. Use full-width purchase actions where appropriate and make discount inputs fit available width. |
| Amenities | Stack details, pricing, entitlement status, and actions. Verify payment-proof flows at phone widths. |
| Coaches | Single-column cards with readable profiles and clear hire/message actions. Avoid cramped metadata and action rows. |
| Guest-locked pages | Keep the explanation and membership action readable and reachable on small screens. |

First pilot: src/GymMembership.App/Views/MyMembershipPage.xaml.

## Phase 5 — Adapt coach journeys

| Page | Planned mobile treatment |
| --- | --- |
| Trainees | Single-column cards with trainee identity and the main action prominent. Stack secondary actions when needed. |
| My shifts | Full-width compact date/time forms. Reflow shift details and management actions. |
| Attendance | Readable session/trainee cards with large attendance controls. Preserve existing confirmations and status rules. |
| My coach profile | Single-column editing, appropriately growing/scrolling multiline fields, and a Save action reachable with the keyboard open. |

Do not introduce new scheduling, attendance, or profile behavior as part of layout work.

## Phase 6 — Adapt shared screens

### Sessions and Time requests

- [ ] Stack dates, times, selectors, and action groups in compact mode.
- [ ] Wrap long titles and names without crowding status chips.
- [ ] Ensure request/rescheduling forms remain usable without pushing the list entirely off-screen.
- [ ] Preserve role-specific visibility and permissions.

### Messages

- [ ] Keep the conversation selector usable at narrow widths.
- [ ] Size message bubbles relative to available width.
- [ ] Keep the composer above the keyboard.
- [ ] Verify scrolling when messages arrive, the keyboard opens, and orientation changes.
- [ ] Preserve draft text and selected conversation during layout changes.

### Alerts and Settings

- [ ] Wrap notification content and timestamps.
- [ ] Make settings/password forms comfortably touchable.
- [ ] Keep errors and Save actions reachable during keyboard use.

### Scrolling constraint

Preserve list virtualization. Do not solve scrolling by placing an entire CollectionView inside a ScrollView. Select one appropriate scrolling owner for each layout.

## Implementation sequence and file boundaries

1. Audit and compact styles.
2. Pilot: My membership and Sessions.
3. Verify the adaptive approach before applying it everywhere.
4. Remaining member pages.
5. Coach-specific pages.
6. Messages and remaining shared pages.
7. Login, Register, and guest-flow checks.
8. Cross-platform regression review.

Keep implementation primarily in:

- src/GymMembership.App/Views/*.xaml and necessary page code-behind.
- src/GymMembership.App/Resources/Styles/Styles.xaml or a scoped adaptive resource dictionary.
- MemberShell/CoachShell only where testing reveals navigation issues.
- src/GymMembership.App/Controls/ only for genuinely reusable layout behavior.

No database or backend changes should be required. Keep each batch small and verifiable.

## Verification plan

### Layout coverage

- [ ] Narrow phones: approximately 320–360 units wide.
- [ ] Typical phones: approximately 390–430 units wide.
- [ ] Tablet-sized layouts.
- [ ] Portrait and landscape.
- [ ] Resizable Windows desktop windows.
- [ ] Breakpoint transitions, including widths just below/above each threshold.

### Interaction and state coverage

- [ ] Keyboard open on every form.
- [ ] Large system text and light/dark themes.
- [ ] Long names/messages, large prices, and lengthy error messages.
- [ ] Empty lists and large lists.
- [ ] Proof selection completed, cancelled, and failed.
- [ ] Loading and repeated taps.
- [ ] Back navigation and role switching.
- [ ] Guest to active member access transition.
- [ ] Active member to expired membership access transition.
- [ ] Existing staff usage of shared pages/resources.

Use an Android emulator and a physical Android device where available. Verify Windows desktop behavior. iOS verification requires a Mac build environment; the project declares its iOS target conditionally on macOS. Record untested platforms explicitly.

### Build and regression checks

- [ ] Android build passes.
- [ ] Windows build passes.
- [ ] Existing tests pass.
- [ ] Manual member journeys pass.
- [ ] Manual coach journeys pass.
- [ ] Review completed changes according to CLAUDE.md before claiming completion.

## Acceptance criteria

- Every member and coach destination is reachable.
- No content or action requires unintended horizontal scrolling.
- Text, status chips, and buttons do not overlap or clip.
- Every form remains usable with the keyboard open.
- Layout changes preserve drafts, selections, and current work.
- Existing permissions and membership locks behave unchanged.
- Desktop layouts remain usable, including shared staff pages.
- Android and Windows builds and existing tests pass.
- Manual mobile journeys are verified, with any platform/device gaps documented.

## Recommendation

First make existing pages fit and work reliably on phones. Treat bottom tabs, new dashboards, and visual redesigns as separate follow-up work, not prerequisites for proper mobile layouts.

## Implementation notes (2026-10-13)

- Breakpoint = `OnIdiom` (Phone vs everything else), the same pattern LoginPage already uses, instead of measured width states. Ceiling: a narrow Windows window or a phone-sized tablet window keeps the wide layout. Upgrade path: a width-based visual state if that matters.
- Small buttons are 48 on Android/iOS and 40 elsewhere; the fixed `HeightRequest` was removed so large system text can grow them. This also applies to staff pages on phones.
- Phone page padding is 16 (was 20) on all member, coach and shared pages. Staff-only pages were not touched.
- Card action columns (`CardActions`, `CardButton`, `CardOutlineButton` in Styles.xaml) drop under the text, full width, on phones. Used on Packages, Amenities, Coaches, Trainees and My membership.
- My membership: QR sits above its details on phones; the reference field and send button stack (they also stack on desktop now).
- Sessions and Time requests action rows wrap (FlexLayout) instead of overflowing.
- Pages not changed because they already fit: Attendance, My shifts, My coach profile, Messages, Alerts, Settings, Login, Register, LockedPage.
- Navigation (shells) untouched.
