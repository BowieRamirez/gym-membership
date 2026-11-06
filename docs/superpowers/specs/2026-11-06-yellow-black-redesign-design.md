# Yellow and black redesign ("unslop") — design

Date: 2026-11-06. Owner decisions from the brainstorming conversation of that day. Becomes Phase 9 of `docs/superpowers/plans/2026-11-05-security-and-admin-cleanup.md`.

## Intent

The owner wants every screen to look calm and deliberate rather than generated ("no clutter and clean"). The standard is the Overview page rebuilt in Phase 7 (`AdminOverviewPage.Windows.cs`, mockup `docs/mockups/admin-overview.html`): one strip of numbers only where numbers help, plain panels with thin dividing lines, short lists that lead to full pages, and nothing on screen that doesn't help someone do their job. At the same time the app takes on the gym's identity: a yellow-and-black colour scheme and the "Health & Fitness Gym Center" badge.

Work goes through the `impeccable` skill, alongside `frontend-design`, as `CLAUDE.md` and the owner's memory require.

## Decisions (owner, 2026-11-06)

- **D22 — scope.** Windows: every page for every account type (member, coach, front desk, Admin, System admin), including sign-in, registration and the locked screen. Android: member and coach screens only (plus sign-in and registration, which members use). Android front-desk and admin screens keep their layouts.
- **D23 — colours.** Yellow and black, after the owner's reference dashboard (yellow side menu, black cards and buttons, light grey page, white panels). Both light and dark themes stay.
- **D24 — logo.** The full badge (`docs/screenshotsforexample/882263184_1999024240766862_3446343023741622644_n.jpg`) where there is room; option B, an "HF" monogram in the badge's ring, everywhere small. Chosen in the visual companion over a ring-and-barbell mark.
- **D25 — name.** "Health & Fitness Gym Center" in full where there is room (sign-in and registration beside the badge); "Gym Center" as the short name where space is tight (window title bar, Android app name under the icon, side-menu logo tooltip).

## Goals

1. One colour system, in `Resources/Styles/Colors.xaml` and `Styles.xaml`, that every screen draws from, in light and dark.
2. The new logo and name everywhere the old dumbbell and "Gym Membership" appear.
3. Every in-scope page reworked to the Overview standard, one group at a time, each checked in the demo app.
4. Nothing breaks: AutomationIds, behaviour, and phone layouts outside the scope stay as they are.

## Non-goals

- No behaviour, rule or database changes. A page may lose clutter but not lose a function: every action and every fact that is on a page today stays reachable.
- No new features hidden inside the redesign.
- Android front-desk and admin layouts are not reworked (D22). They inherit the new colours because colours are shared; they must still read correctly, but they are not rearranged.
- The gym's name stored in Gym settings (live data) is not edited by the implementer; the owner can change it there so receipts match.

## Anti-goals (what would make this a failure even if it works)

- Yellow everywhere. Yellow is the signal colour; if every surface is yellow, nothing stands out.
- A generic "dashboard kit": identical rounded cards for everything, decorative gradients, icon-plus-heading-plus-text tiles as page structure.
- Text on yellow in anything but black; grey text on yellow.
- A logo that blurs into a smudge at small sizes.
- Losing a control the scripts or the owner rely on (AutomationIds, keyboard focus, screen-reader names).

## The colour system (D23)

Implementer's starting values, to be confirmed in the Phase 9 mockup:

| Role | Light | Dark |
| --- | --- | --- |
| Signal yellow | `#FFEC00` | `#FFEC00` |
| Ink (text, black surfaces) | `#111111` | `#F2F2F2` |
| Page | `#EEEEEE` | `#0E0E0E` |
| Panel / card | `#FFFFFF` | `#1A1A1A` |
| Raised / field | `#F5F5F5` | `#242424` |
| Line | `#E4E4E4` | `#2A2A2A` |
| Muted text | `#5B5B5B` | `#A6A6A6` |
| Side menu | yellow, black icons, selected item black with yellow icon | `#161616`, light icons, selected item yellow with black icon |

Rules:
- Yellow is for the side menu (light), the selected menu item, the one highlighted figure on a page, attention bars, the main action, and "needs action" tags. Black takes the same roles in reverse (a black highlighted figure in light, a yellow one in dark).
- Text on yellow is always black. Text on black is white or yellow.
- Meaning colours stay only where colour carries meaning (verified, rejected, pending, available): green, red and amber, adjusted to sit with yellow and black, and always next to their word. Item 15's Active / Inactive / Deleted words stay.
- Contrast: body text at least 4.5:1 and large text at least 3:1 in both themes. Muted text is checked against both panel and page.
- Fonts stay Barlow and Barlow Condensed (already shipped).
- The existing names in `Colors.xaml` (Lime, Chalk, Plate*, etc.) are renamed or remapped so the code says what it means; every use is updated, none left pointing at an old green.

## Logo and name (D24, D25)

- **Full badge**: sign-in and registration screens (`AuthShell`, `LoginPage`, `RegisterPage`), shown large enough to read its lettering (at least about 140 px). It is a black-on-white picture, so it sits on a white or light round; in dark theme it keeps its white round rather than being inverted.
- **HF monogram**: redrawn as vector artwork (yellow "HF" in a yellow ring on a black disc), not a shrunk picture. Used for the side-menu logo (replacing `DumbbellLogo`), the Windows window icon, the Android app icon (`Resources/AppIcon/appicon.svg` and `appiconfg.svg`), the splash screen (`Resources/Splash/splash.svg`), and the phone headers where the dumbbell is shown today.
- **Name**: "Gym Center" replaces "Gym Membership" in `App.xaml.cs` (window and title bar), the member shell title, the side-menu tooltip and screen-reader name (`IconRail.Windows.cs`), the `Styles.xaml` heading, and the Android app label. "Health & Fitness Gym Center" is used beside the full badge. The demo's seeded gym name becomes "Health & Fitness Gym Center" so demo receipts match. `ApplicationTitle` in `GymMembership.App.csproj` (today `GymMembership.App`) becomes "Gym Center"; it is the Android label and the Windows display name. `ApplicationId` does not change (it would install as a different app).
- The badge ships as an app image in `Resources/Images` under a name MAUI accepts (lower case, letters, digits and underscores, for example `gym_badge.png`), trimmed to the circle; the source photo stays in `docs/screenshotsforexample/`.

## The page standard

Every in-scope page is reworked against these, in this order of importance:

1. **One purpose, one main action.** The page says what it is for by its layout, not by a paragraph. The main action is the one yellow (or black) button.
2. **Lists are tables or plain panels with dividers**, not stacks of identical cards; on narrow windows and phones they fall back to compact stacked rows (the existing `TableBody` pattern).
3. **No duplication.** No in-page title repeating the window header (Phase 7 found this), no buttons repeating the side menu, no explanatory paragraphs that restate the page. Help text stays only where a rule would otherwise surprise someone (for example refunds and voids), and is one sentence.
4. **Numbers in a strip only where they help a decision.**
5. **Empty, loading and error states** say what happened and what to do next.
6. **States and access**: keyboard focus visible, every icon-only button has a screen-reader name, AutomationIds kept on the same controls.

## Pages in scope

Windows (all through `StaffShell`, `MemberShell` and `CoachShell`):

| Group | Pages |
| --- | --- |
| Entry | Sign-in (`LoginPage`), registration (`RegisterPage`), locked (`LockedPage`) |
| Member | My membership, Packages, Amenities, Book an amenity, Coaches, Sessions, Time requests, Messages, Settings |
| Coach | Dashboard (`AttendancePage`), Sessions, Time requests, Trainees, My shifts, My coach profile, Amenities, Messages, Settings |
| Front desk | Home, Payments to verify, Cashier, Check-in, Amenity status (with verify amenity use), Messages, Settings |
| Admin and System admin | Overview (recolour only; it is the standard), Users and roles, Packages and amenities, Discounts, Cancellations and refunds, Void a payment, Gym settings, Revenue, Logs and Archive (with the Archive panel) |

Android: the Entry group and the Member and Coach groups above, in their phone layouts (`PhoneNavigation`, `PhonePageTemplates`, `PhoneMorePage`, bottom bar).

Shared pieces first: `Colors.xaml`, `Styles.xaml`, `Controls.cs` (StatusChip, Banner, CardTitle, TableBody, CardGrid), `IconRail.Windows.cs`, `HeaderBar`, `IconView` chips. Most pages change mostly through these.

## How it is built and checked

- A mockup first, in `docs/mockups/`, using the real tokens: the sign-in screen with the badge, one member page and one coach page on Windows, and one member screen on Android, each in light and dark. The owner approves it before the app changes.
- Then the shared pieces, then one group of pages at a time. Each group is checked in a Windows demo build at about 900 px and maximised, light and dark; Android groups on the Pixel 7 emulator (`pixel_7_-_api_36_0`) in light and dark.
- Existing tests keep passing; `PageLayoutTests` rules (HeroPanel labels black, buttons Ink) are updated to the new names, not deleted. `scripts/Test-WindowsAdminNavigation.ps1` passes after each group.
- `impeccable` review of each group before moving on; `ponytail-review` on the diff at the end.
- No database change, so nothing to deploy for this phase.

## Left to the builder

- Exact shades after the contrast check, and the meaning colours' final values.
- How each page's clutter is cut, within the standard above; anything that would remove a function goes back to the owner.
- The order of groups after the shared pieces (suggested: Entry, Member, Coach, Front desk, Admin).
