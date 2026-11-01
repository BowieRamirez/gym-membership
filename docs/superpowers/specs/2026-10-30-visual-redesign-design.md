# Visual redesign: sage, lime and black (Windows and Android)

Date: 2026-10-30. Status: approved by the owner and built (branch `visual-redesign-2026-10-30`). The page layouts follow the later page mockup, see `docs/superpowers/plans/2026-10-30-visual-redesign-pages.md`.

## Intent

The owner thinks the app doesn't look modern. They shared a reference dashboard ("Managing Your Team and Workflows": pale sage background, slim icon side bar, big light title, pill tabs, large rounded cards, one lime card, black pill buttons) and asked for the app to take inspiration from it. **This is a restyle only.** Every page keeps the same content, buttons, flows and rules it has today. Nothing new gets added to the system.

Approved mockups: `.superpowers/brainstorm/1028-1793357470/content/look-v1.html` (layout direction) and `look-v2.html` (white side menu, black icons, dark mode, logo options).

## Goals

- A new look on every page for every role, from the sign-in screen to the last admin page, on **Windows and Android**.
- Light mode and dark mode, both following the system theme as the app does now.
- A new app logo and icon (the dumbbell), replacing the .NET placeholder icon and the purple splash screen.
- All icons are **black**.

## Non-goals and anti-goals

- No new features, pages, tabs, stats, charts or numbers. The mockups had sample tabs and numbers to show the style; they are not to be built. Re-arranging content that is already on a page into cards is fine.
- Android phone layouts keep their structure (bottom navigation, More page, stacked cards). Android only picks up the new colors, shapes, type and icon treatment.
- No changes to the Core project, view models, services or database.
- It's a failure if any text or icon ends up hard to read (it has to pass contrast in both themes), or if the Windows icon-only menu leaves a page impossible to identify.

## Design tokens (owner approved, from the mockups)

| Token | Light | Dark |
|---|---|---|
| Backdrop (around content, shell) | `#DDE3DA` sage | `#0E100E` |
| Page canvas | `#F2F4F0` | `#161816` |
| Card | `#E7EBE4` | `#212521` |
| Chip / field / inner surface | `#FFFFFF` | `#E7EBE4` for icon chips; `#2C302B` for fields and inner rows |
| Text | `#000000` | `#EEF1EC` |
| Muted text | `#6B726A` | `#A3AAA1` |
| Highlight | `#E4F58A` lime | `#E4F58A` lime |
| Primary button | black, white text | lime, black text |
| Side menu (Windows) | `#FFFFFF` | `#212521` |

- **Status colours stay meaningful**: the existing green/yellow/red/blue status colours (`Plate*`) are kept for status dots and chips (colour plus a word, as now). They may be retuned for contrast on the new surfaces. Lime is used for highlight (the current menu item, one featured card per page at most, the "verified/active" chip background is the builder's call).
- **Dark-mode controls** *(builder's call)*: selected pill tabs are lime with black text (matching the dark primary button), unselected tabs `#2C302B`; all fields, including sign-in, use `#2C302B` with light text.
- **Shapes**: cards have big rounded corners (about 20 to 24), fields about 14, buttons, tabs, chips and badges are full pills. Flat, no shadows, no hairline borders on cards (fields can keep a subtle border for focus).
- **Type**: keep Barlow (already bundled). Page titles get bigger and lighter (medium weight, large size, slight negative letter spacing) like the reference. Keep Barlow Condensed for numbers.
- **Icons**: the existing Lucide line icons (`Controls/Icons.cs`), stroked black (`#000000`) in both themes. In dark mode an icon never sits straight on a dark surface: it always sits on a light chip (`#E7EBE4`) or on lime. In light mode icons in cards sit on a white rounded chip, as in the reference. Icons are sized to sit centred in their chip with even padding (about 18 px icon in a 36 px chip).

## Windows

- **Side menu**: a white (dark: `#212521`) rounded vertical strip on the left, **icons only**. The dumbbell logo sits at the top. Each page gets a fitting Lucide icon. The current page's icon sits on a lime rounded square. **Hovering shows the page name as a tooltip.** Screen-reader names stay the full page titles. Sign out stays at the bottom of the strip as an icon (log-out icon, tooltip "Sign out"), and the profile picture sits at the bottom too, as in the reference.
- **Admin groups (owner decided, mockup `admin-strip.html` option B, opening downward)**: top to bottom: logo, Overview, one briefcase icon for "Front desk operations" (tooltip and screen-reader name say so, plus expanded/collapsed state as today), thin divider, the administration icons (admin and super pages), thin divider, Messages and Settings, then Sign out and the profile picture at the bottom. Clicking the briefcase opens the five front desk icons (Home, Payments to verify, Cashier, Check-in, Amenity Status) downward inside the strip, directly under it, on a light grouped background; the icons below slide down. Clicking it again folds them up. While the current page is a front desk page and the group is folded, the briefcase shows the lime "current" highlight. The strip scrolls if the window is too short.
- **Front desk staff** (non-admin) get no group: their pages are plain icons in the strip.
- **Profile picture** *(builder's call)*: on Windows it moves out of the header into the bottom of the strip (tapping still opens Settings); the Windows header becomes title plus bell. Android keeps it in the header.
- **Header**: the page title becomes the big light title of the reference; the bell becomes a round white button with the black bell icon; the existing red unread badge stays.
- **Pages**: content sits on the canvas in large rounded cards; existing tab-like switches become pill tabs (black when selected, white otherwise); existing list rows become white rounded rows inside cards; existing status chips become pills.
- **Sign-in and Create an account**: left panel black (dark: card colour) with the dumbbell logo, the app name and the existing tagline; right side the existing form with white rounded fields and pill buttons. Same text and controls as now.

## Android

Same tokens, shapes, type and icon rules. Bottom navigation stays the native Shell tab bar (`Controls/PhoneNavigation.cs`) with its items and behaviour. *(Builder's call)*: the bar background is light in both themes (`#FFFFFF` light, `#E7EBE4` dark) so its tinted icons can stay black without per-icon chips; the selected item gets the native selected indicator in lime. No custom bar. Header, sign-in and register pages restyle as on Windows within their current phone layouts.

## Logo

The approved "dumbbell": a lime dumbbell (two upright rounded plates joined by a bar) on a black rounded square. Used as the app icon (Android launcher, Windows taskbar and installer), the splash screen (black background) and in the side menu and sign-in panel. Built as SVG files in `Resources/AppIcon` and `Resources/Splash`; the app's in-page logo is drawn as a vector, not an image, like the other icons.

## Constraints

- .NET MAUI, existing XAML styles in `Resources/Styles/Colors.xaml` and `Styles.xaml` are the main lever; about 70 places use the shared `Panel` style and most colours go through `AppThemeBinding`, so most of the change is central. The 17 hard-coded hex colours and 17 `SetAppThemeColor` calls in pages and controls need updating by hand.
- Vector icon paths must use only absolute M/L/C/Q/Z commands, no arcs (Windows crashes on MAUI's arc conversion; `IconTests` checks every outline). New icons go through the existing arcs-to-curves approach.
- Don't reintroduce `RelativeSource AncestorType` bindings; list pages keep `BasePage.Row<TVm>` handlers.
- The existing tests must keep passing; UI checks in demo mode (move `Resources/Raw/supabase.json` aside, restore it after, never `git add -A` while the `.bak` exists).
- Work stays on branch `phase1-archive-checkpoint-2026-10-20` unless the owner says otherwise (ask before new branches).

## Left to the builder

- Exact icon choice per page, exact radii and spacing, the order of work, and how to split the restyle into steps.
- How each page's existing content maps into cards, as long as nothing is added or removed.
