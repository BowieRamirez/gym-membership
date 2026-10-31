# Visual Redesign Implementation Plan

> **Status (2026-10-30): built.** Tasks 1-7 are done and committed (b975c80..6527604), reviewed by a fresh reviewer (no critical findings; its two important points fixed in e654113). Work is on branch `visual-redesign-2026-10-30` on GitHub, not merged into main. Part 2, the page layouts, is in `2026-10-30-visual-redesign-pages.md`. Part 3, the sign-in screens (2026-10-31), is at the end of this file.
> **Still open:** the owner's device checks in Task 7 step 4 (unticked below). Deferred small items are listed in `docs/progress-2026-10-16.md` section 8.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restyle every page of the Windows and Android app (sign-in to the last admin page, light and dark) in the approved sage / lime / black look, with an icon-only Windows side menu and a new dumbbell logo, without adding or removing any feature.

**Architecture:** Most of the look lives in `Resources/Styles/Colors.xaml` and `Styles.xaml`, so tokens and shared styles change first and every page follows. Icons get one rule in `IconView` (black stroke on a light chip). The Windows side menu becomes one Windows-only builder (`Controls/IconRail.Windows.cs`) used by all three shells, replacing the admin text menu. A vector `DumbbellLogo` control plus new icon/splash SVGs carry the logo. A final sweep fixes the few hard-coded colours on pages.

**Tech Stack:** .NET 10 MAUI (Android + WinUI), XAML styles with `AppThemeBinding`, xUnit source-text tests (`tests/GymMembership.Tests`), Lucide icons as arc-free path data.

**Spec:** `docs/superpowers/specs/2026-10-30-visual-redesign-design.md` (mockups: `.superpowers/brainstorm/1028-1793357470/content/look-v2.html`, `admin-strip.html`)

## Global Constraints

- Restyle only: no new pages, tabs, stats, numbers or actions; no changes in `src/GymMembership.Core` or the database.
- Light/dark follow the system theme through `AppThemeBinding` / `SetAppThemeColor`, as now.
- Every icon is stroked `#000000` in both themes and sits on a light chip (`#FFFFFF` light, `#E7EBE4` dark) or on lime.
- Icon path data: only absolute M/L/C/Q/Z with space-separated numbers, no arcs (`IconTests` runs them through MAUI's parser).
- No `RelativeSource AncestorType` bindings; list pages keep `BasePage.Row<TVm>`.
- Font stays Barlow (bundled); no new packages.
- Status colours (`Plate*`) keep their meaning (blue scheduled, yellow pending, green verified, red rejected); lime is highlight only.
- Text pairs reach 4.5:1, indicators 3:1 (`ContrastTests`).
- Work on branch `phase1-archive-checkpoint-2026-10-20`; `git add` explicit paths only (never `-A`, `.` or `docs`); never commit while `Resources/Raw/supabase.json.bak` exists.
- Demo-mode UI checks: move `src/GymMembership.App/Resources/Raw/supabase.json` to `supabase.json.bak`, check, move it back and confirm.

Commands used throughout:
- Tests: `dotnet test tests/GymMembership.Tests` (expect 0 failed; 725 passed before this work, the count changes as tests are edited)
- Windows build: `dotnet build src/GymMembership.App -p:PlatformOnly=windows`
- Android build: `dotnet build src/GymMembership.App -p:PlatformOnly=android`

## Review Focus

1. **Dark mode, every page**: an icon, label or button left on a dark surface without its chip or with dark text is invisible. Expect everything readable. (Task 6 screenshots every page in dark mode.)
2. **Admin rail, front desk page current while the group is folded**: the briefcase must show the lime highlight, and the tooltip / screen-reader name must say which page is current. (Task 4 test.)
3. **Short Windows window (720 px tall)**: an admin rail with the group open has 20+ icons; it must scroll, not clip Sign out or the profile picture. (Task 4 test asserts the middle section is in a `ScrollView`; Task 6 checks on screen.)
4. **Sub-admin and front desk accounts**: sub-admins lose the `super` pages and front desk accounts have no group; the rail must not show an empty divider run or a briefcase with nothing in it. (Task 4 test.)
5. **Members-only pages for a guest** (locked pages): their rail icons must still show, open the locked page, and have tooltips. (Task 4 test covers every MemberShell title having an icon.)

---

### Task 1: Colour tokens and shared styles

**Files:**
- Modify: `src/GymMembership.App/Resources/Styles/Colors.xaml`
- Modify: `src/GymMembership.App/Resources/Styles/Styles.xaml`
- Test: `tests/GymMembership.Tests/ContrastTests.cs`

**Interfaces:**
- Produces (colour keys every later task uses):
  - Existing keys, new values: `ChalkLight #F2F4F0`, `ChalkDark #161816` (page canvas); `SurfaceLight #FFFFFF`, `SurfaceDark #2C302B` (fields, inner rows); `InkLight #000000`, `InkDark #EEF1EC`; `MutedLight #5A6159`, `MutedDark #A3AAA1`; `LineLight #D4DAD0`, `LineDark #3A3F39`; `Chalk #F2F4F0`.
  - New keys: `CardLight #E7EBE4`, `CardDark #212521` (Panel, cards, Windows rail in dark); `BackdropLight #DDE3DA`, `BackdropDark #0E100E` (Shell background behind the rail and title bar); `Lime #E4F58A`; `IconInk #000000`; `IconChipLight #FFFFFF`, `IconChipDark #E7EBE4`; `RailLight #FFFFFF`, `RailDark #212521`.
  - `Plate*` keys keep their names; retune values only if a contrast test fails.
  - New style keys: `PageTitle` (Label: `BarlowMedium`, size 34 on Windows / `TextTitle2` on phones, `CharacterSpacing -0.4`, `LineHeight 1.05`), `LimeButton` and `LimeSmallButton` (lime background, black text, based on `BaseButton` / `SmallButton`).

- [x] **Step 1: Extend `ContrastTests` with the new pairs**

Add to `TextPairs()`: `InkLight`/`MutedLight` on `CardLight`, `InkDark`/`MutedDark` on `CardDark` and `SurfaceDark`; `Black` on `Lime` (lime buttons, lime highlight); `IconInk` on `IconChipLight` and `IconChipDark`. Change the dark inverse pair to `("Black", "Lime")` since the dark primary button is lime with black text. Add `Card` to the backgrounds in `IndicatorPairs()` (`from bg in new[] { "Chalk", "Surface", "Card" }`).

- [x] **Step 2: Run, expect FAIL** (`CardLight` etc. not found)

Run: `dotnet test tests/GymMembership.Tests --filter ContrastTests`

- [x] **Step 3: Set the token values above in `Colors.xaml`** (keep the comments short; replace the "cool concrete / rubber flooring" comment with "sage canvas, lime highlight").

- [x] **Step 4: Restyle the shared styles in `Styles.xaml`**

- `Shell`: `BackgroundColor` → Backdrop; `TabBar*` left to Task 5.
- `BaseButton`: background Ink light / `Lime` dark; text `White` light / `Black` dark; `CornerRadius 24`.
- `SmallButton`, `SmallOutlineButton`: `CornerRadius {OnPlatform Android=24, Default=20}`.
- `OutlineButton`: unchanged colours (ink border/text); inherits radius.
- `LimeButton`, `LimeSmallButton`: new (Interfaces).
- `Field`: `RoundRectangle 14`, stroke `Line`, background `Surface`.
- `Panel`: `RoundRectangle 22`, `StrokeThickness 0`, background `CardLight`/`CardDark`, padding 18. `ErrorPanel` / `NoticePanel` set `StrokeThickness 1.5` and their plate stroke again (they inherit 0 now).
- `Chip`: `RoundRectangle 12`, `StrokeThickness 0`, background `Surface`, padding `10,4`.
- `Display`: `BarlowMedium`, `CharacterSpacing -0.6`. Add `PageTitle`.

- [x] **Step 5: Run tests, expect PASS.** If a `Plate*` pair fails, darken (light) or lighten (dark) that plate value until it passes, and note the old value in the comment as the file already does.

Run: `dotnet test tests/GymMembership.Tests` → 0 failed.

- [x] **Step 6: Build Windows, then commit**

```bash
git add src/GymMembership.App/Resources/Styles/Colors.xaml src/GymMembership.App/Resources/Styles/Styles.xaml tests/GymMembership.Tests/ContrastTests.cs
git commit -m "style: sage, lime and black colour tokens and pill/rounded shared styles"
```

---

### Task 2: Black icons on light chips everywhere

**Files:**
- Modify: `src/GymMembership.App/Controls/Icons.cs` (`IconView`, `EyeButton`, `IconButton`)
- Modify: `src/GymMembership.App/Controls/HeaderBar.xaml` (bell)
- Test: `tests/GymMembership.Tests/IconTests.cs`

**Interfaces:**
- Consumes: `IconInk`, `IconChipLight`, `IconChipDark`, `LimeSmallButton` (Task 1).
- Produces: `IconView` with `Icon` (string), new `Size` (double, icon size, default 18) and `ChipSize` (double, default 36); it draws a rounded chip (`RoundRectangle` radius = ChipSize / 3, `#FFFFFF` light / `#E7EBE4` dark) with the black path centred. `OnInk` is removed. `IconView.Selected` (bool, default false): chip becomes `Lime`. Later tasks use `new IconView { Icon = "...", Selected = true }`.

- [x] **Step 1: Add tests to `IconTests`**

- `Icon_view_is_black_in_both_themes`: source of `Controls/Icons.cs` contains `"IconInk"` and does not contain `"InkDark"` or `OnInk`.
- `Bell_is_drawn_by_the_shared_icon_view`: `Controls/HeaderBar.xaml` contains `Icon="bell"` and no `<Path`.
- `Selected_choice_buttons_are_lime_not_black`: `Icons.cs` contains `"LimeSmallButton"`.

- [x] **Step 2: Run, expect FAIL.** `dotnet test tests/GymMembership.Tests --filter IconTests`

- [x] **Step 3: Implement**

- `IconView`: content becomes a `Border` (the chip) holding the `Path`; stroke from `IconInk` (one colour, no `SetAppTheme` on the stroke); chip background `SetAppThemeColor(IconChipLight, IconChipDark)`, or `Lime` when `Selected`. `WidthRequest`/`HeightRequest` = `ChipSize`, path size = `Size`.
- Add `["bell"]` to `Icons.Data` (Lucide bell, arc-free: `M 6 8 C 6 4.686 8.686 2 12 2 C 15.314 2 18 4.686 18 8 C 18 15 21 17 21 17 L 3 17 C 3 17 6 15 6 8 Z M 10.3 21 C 10.646 21.598 11.29 22 12 22 C 12.71 22 13.354 21.598 13.7 21`).
- `HeaderBar.xaml`: replace the bell `Path` with `<c:IconView Icon="bell" ChipSize="40" Size="20" InputTransparent="True" .../>`; keep the button, badge and sizes.
- `EyeButton`: `_icon` gets `ChipSize = 32, Size = 18`.
- `IconButton.Refresh`: selected uses `LimeSmallButton`, unselected `SmallOutlineButton`; drop `_icon.OnInk`.

- [x] **Step 4: Run all tests, expect PASS; build Windows.**

- [x] **Step 5: Commit**

```bash
git add src/GymMembership.App/Controls/Icons.cs src/GymMembership.App/Controls/HeaderBar.xaml tests/GymMembership.Tests/IconTests.cs
git commit -m "style: icons drawn black on a light chip, lime when selected"
```

---

### Task 3: Dumbbell logo, app icon, splash, sign-in and create-account pages

**Files:**
- Create: `src/GymMembership.App/Controls/DumbbellLogo.cs`
- Modify: `src/GymMembership.App/Resources/AppIcon/appicon.svg`, `appiconfg.svg`, `src/GymMembership.App/Resources/Splash/splash.svg`, `src/GymMembership.App/GymMembership.App.csproj:63,66`
- Modify: `src/GymMembership.App/Views/LoginPage.xaml`, `src/GymMembership.App/Views/RegisterPage.xaml`
- Test: `tests/GymMembership.Tests/LogoTests.cs` (new)

**Interfaces:**
- Produces: `DumbbellLogo : ContentView` with `Size` (double, default 40) and `Inverted` (bool: false = lime dumbbell on black square, true = black dumbbell on lime square). Drawn with MAUI shapes (a `Border` square radius Size·0.28 and three `RoundRectangle`/`Rectangle` shapes), no image file. Geometry on a 64-unit grid: square 64 radius 18; plates `x 12, y 22, w 8, h 20, r 3` and `x 44, y 22, w 8, h 20, r 3`; bar `x 20, y 29, w 24, h 6, r 2`. `AutomationProperties.IsInAccessibleTree = false` (decorative; the app name sits beside it).

- [x] **Step 1: Write `LogoTests`**

- `App_icon_is_the_dumbbell_not_the_template`: `appiconfg.svg` contains `#E4F58A` and no `#ffffff`; csproj `MauiIcon` and `MauiSplashScreen` have `Color="#000000"` (no `#512BD4`).
- `Sign_in_and_create_account_show_the_logo`: `LoginPage.xaml` and `RegisterPage.xaml` contain `<c:DumbbellLogo`.

- [x] **Step 2: Run, expect FAIL.**

- [x] **Step 3: Logo assets**

- `appicon.svg`: a 456×456 black (`#000000`) square (Android masks the shape).
- `appiconfg.svg`: 456×456, the lime dumbbell from the geometry above scaled into the centre 60% (safe zone), no background.
- `splash.svg`: the lime dumbbell alone, 456×456, transparent background; csproj `MauiSplashScreen Color="#000000"`, `MauiIcon Color="#000000"`.

- [x] **Step 4: `DumbbellLogo` control** (as Interfaces).

- [x] **Step 5: Sign-in page**

Left panel (desktop): background `InkLight` light / `CardDark` dark, corner radius 24 inside a 12-unit margin on a `Backdrop` page, `Padding 48`; top: `DumbbellLogo Size=48` + "Gym Membership" (`Heading`, white); bottom: the existing tagline in `Display`, white. Remove both `LaneView`s from the panel. Right side: same controls and texts as now; heading "Gym Membership" stays (`Display`); phone gets the `DumbbellLogo` in place of its `LaneView`. Buttons keep their current styles (now pills from Task 1).

- [x] **Step 6: Create-account page**: replace its `LaneView` with `DumbbellLogo Size=40`; nothing else changes beyond what the shared styles do.

- [x] **Step 7: Tests PASS, Windows and Android builds succeed. Demo-mode check of sign-in and create-account in light and dark (screenshots).**

- [x] **Step 8: Commit**

```bash
git add src/GymMembership.App/Controls/DumbbellLogo.cs src/GymMembership.App/Resources/AppIcon/appicon.svg src/GymMembership.App/Resources/AppIcon/appiconfg.svg src/GymMembership.App/Resources/Splash/splash.svg src/GymMembership.App/GymMembership.App.csproj src/GymMembership.App/Views/LoginPage.xaml src/GymMembership.App/Views/RegisterPage.xaml tests/GymMembership.Tests/LogoTests.cs
git commit -m "feat: dumbbell logo, app icon and splash; restyled sign-in and create account"
```

---

### Task 4: Windows icon-only side menu for every role

**Files:**
- Create: `src/GymMembership.App/Controls/IconRail.Windows.cs`
- Modify: `src/GymMembership.App/StaffShell.xaml.cs`, `MemberShell.xaml.cs`, `CoachShell.xaml.cs` (call the rail on Windows)
- Delete: `src/GymMembership.App/StaffShell.Windows.cs` (admin text menu, replaced)
- Modify: `src/GymMembership.App/Controls/HeaderBar.xaml`, `HeaderBar.xaml.cs` (Windows: no avatar, no `SignOutFooter`, title uses `PageTitle`, height 72)
- Modify: `src/GymMembership.App/Controls/Icons.cs` (new page icons)
- Test: rewrite `tests/GymMembership.Tests/WindowsAdminNavigationTests.cs` (old text-menu assertions go); add `tests/GymMembership.Tests/IconRailTests.cs`

**Interfaces:**
- Consumes: `IconView` (`Icon`, `Selected`, `ChipSize`, `Size`) from Task 2; `DumbbellLogo` from Task 3; `RailLight`/`RailDark`, `BackdropLight`/`BackdropDark`, `Lime` from Task 1; `HeaderViewModel` (`SignOutCommand`, `Letters`, `AvatarUrl`) as `HeaderBar` uses it.
- Produces: `static class IconRail` (`#if WINDOWS`) with `public static void Install(Shell shell, string roleName, HeaderViewModel header, bool groupFrontDesk)` and `internal static readonly Dictionary<string, string> PageIcons` (page title → icon name).

Layout (top to bottom), inside a `Border` (`RoundRectangle 22`, background Rail, margin 12, width 64) on the Backdrop:
1. `DumbbellLogo Size=40`, tooltip = `"Gym Membership, " + roleName` (keeps the role the old flyout header showed).
2. A `ScrollView` holding the page icons (Review Focus 3).
3. Bottom, outside the scroll: Sign out (`IconView Icon="log-out"`, tooltip / description "Sign out", runs `header.SignOutCommand`), then `Avatar` 34 (tooltip "Settings"; tap opens the Settings item, as the header avatar does today).

Page icons: one `Grid` per Shell item = transparent `Button` (48×48, click target, `SemanticProperties.Description` = title, plus ", current page" when current) + `IconView` on top (`Selected` when current). `ToolTipProperties.SetText(button, item.Title)`. Clicking sets `shell.CurrentItem = item` (same as today: never replace or remove Shell items, never set native selection).

Grouping:
- `groupFrontDesk == true` (admin, sub-admin): Overview; briefcase toggle; the five `ClassId == "desk"` items in a `VerticalStackLayout` with `IconChipLight`/`CardDark`-tinted rounded background, `IsVisible = false` at start, shown under the briefcase; divider; `admin` + `super` items; divider; Messages and Settings.
- `groupFrontDesk == false` (front desk, member, coach): all items in Shell order except Settings and Messages; divider; Messages, Settings.
- A divider (1.5 high, 28 wide, `LineLight`/`LineDark`) is only added between two non-empty runs.
- Briefcase: icon `briefcase`; tooltip "Front desk operations"; description "Front desk operations, expanded/collapsed" plus ", current page: {title}" when a desk page is current; `Selected` when a desk page is current **and** the group is folded (Review Focus 2). Clicking toggles the group.
- Refresh selection on `shell.Navigated`.

Shell setup on Windows: `shell.FlyoutContent = rail`, `shell.FlyoutHeader = null`, `shell.FlyoutFooter = null`, `shell.FlyoutWidth = 88`, `Shell.FlyoutBackgroundColor` = Backdrop. The header's existing collapse toggle (`HeaderBar.Windows.cs`) keeps working with the new width; leave it.

`PageIcons` (all must exist in `Icons.Data`; add the missing ones as arc-free Lucide outlines: convert circles/arcs to cubic curves with a throwaway script outside the repo, then paste):
Overview `layout-dashboard`, Home `house`, Payments to verify `badge-check`, Cashier `banknote`, Check-in `scan-line`, Amenity Status `waves`, Users and roles `users`, Packages and amenities `package`, Discounts `tag`, Cancellations & refunds `undo-2`, Void a payment `ban`, Gym settings `sliders-horizontal`, Revenue `chart-line`, Logs & Archive `archive`, Messages `message-square`, Settings `settings`, My membership `id-card`, Packages `package`, Amenities `waves`, Book an amenity `calendar-plus`, Coaches `dumbbell`, Sessions `calendar-days`, Time requests `clock`, Dashboard `layout-dashboard`, Trainees `users`, My shifts `calendar-clock`, My coach profile `user-round`. Plus `briefcase`, `log-out`.

- [x] **Step 1: Write `IconRailTests`**

- `Every_page_in_every_shell_has_a_drawn_icon`: titles from the three `*Shell.xaml` files (`FlyoutItem Title`, unescaped `&amp;`) plus "Overview" are all keys of `PageIcons` (regex `\["([^"]+)"\]\s*=\s*"([a-z-]+)"` over `IconRail.Windows.cs`), and every value is in `IconTests.Drawn()` (make `Drawn` `internal static`).
- `Rail_is_Windows_only_and_scrolls`: source starts `#if WINDOWS`, ends `#endif`, contains `new ScrollView`, `ToolTipProperties.SetText`, `FlyoutWidth = 88`, and not `Items.Remove`, `new ShellContent`, `MenuItemsSource`, `SelectedItem`.
- `Front_desk_group_starts_folded_and_marks_the_briefcase_when_a_desk_page_is_current`: source contains `IsVisible = false`, `"Front desk operations"`, `", current page: "`, and the selection expression for the briefcase includes `!group.IsVisible`.
- `Dividers_only_between_non_empty_runs`: source contains a guard such as `if (above.Count > 0 && below.Count > 0)` (name the helper `AddRun`; assert `AddRun(` is used for each run).
- `All_shells_install_the_rail_on_Windows`: each `*Shell.xaml.cs` contains `IconRail.Install(` inside `#if WINDOWS`; `StaffShell.xaml.cs` passes `groupFrontDesk: role is AppRole.Admin or AppRole.SubAdmin`.

Rewrite `WindowsAdminNavigationTests`: keep `Overview_is_a_Windows_only_landing_item...` and `All_existing_pages_and_routes_remain_in_the_shared_shell` (they are still true); drop the text-menu typography tests; change `Grouping_is_Windows_only_and_admin_only` to expect `SetupAdminOverview(...)` then `IconRail.Install(...)`.

- [x] **Step 2: Run, expect FAIL.**

- [x] **Step 3: Add the missing icons to `Icons.Data`; run `IconTests`, expect PASS.**

- [x] **Step 4: Implement `IconRail.Install`** (as Interfaces / Layout / Grouping).

- [x] **Step 5: Wire the shells**; delete `StaffShell.Windows.cs`; `HeaderBar` on Windows: hide `Avatar`, stop setting `FlyoutFooter`, `TitleLabel.Style = PageTitle`, `HeightRequest 72`. Delete `SignOutFooter.xaml(.cs)` if nothing else uses it (grep first).

- [x] **Step 6: Tests PASS; Windows build succeeds.**

- [x] **Step 7: Demo-mode walkthrough on Windows**: sign in as each demo role (Member, Coach, Front desk, Admin), hover three icons (tooltips), open/close the briefcase, open a desk page then fold the group (briefcase lime), shrink the window to ~720 px tall (rail scrolls, Sign out visible), sign out from the rail. Light and dark. Screenshots.

- [x] **Step 8: Commit**

```bash
git add src/GymMembership.App/Controls/IconRail.Windows.cs src/GymMembership.App/StaffShell.xaml.cs src/GymMembership.App/MemberShell.xaml.cs src/GymMembership.App/CoachShell.xaml.cs src/GymMembership.App/Controls/HeaderBar.xaml src/GymMembership.App/Controls/HeaderBar.xaml.cs src/GymMembership.App/Controls/Icons.cs tests/GymMembership.Tests/IconRailTests.cs tests/GymMembership.Tests/WindowsAdminNavigationTests.cs tests/GymMembership.Tests/IconTests.cs
git rm src/GymMembership.App/StaffShell.Windows.cs
git commit -m "feat(windows): icon-only side menu with hover names and folding front desk group"
```
(add the `SignOutFooter` files to `git rm` if deleted)

---

### Task 5: Android bottom bar and phone pages

**Files:**
- Modify: `src/GymMembership.App/Controls/PhoneNavigation.cs:92-95,153-170`
- Modify: `src/GymMembership.App/Views/PhoneMorePage.cs` (its hard-coded colours)
- Test: `tests/GymMembership.Tests/PhoneNavigationTests.cs`

**Interfaces:**
- Consumes: Task 1 tokens. Produces nothing new.

- [x] **Step 1: Test** `Bottom_bar_is_light_in_both_themes_with_black_icons`: `PhoneNavigation.cs` contains `"#E7EBE4"` (dark bar) and `TabBarForegroundColorProperty, Color.FromArgb("#000000"), Color.FromArgb("#000000")`, and `ItemActiveIndicatorColor` (lime `#E4F58A`); no `#24282C`, `#1F2226`, `#ECEEF0`. `PhoneMorePage.cs` has no `#24282C` / `#1F2226`.

- [x] **Step 2: Run, expect FAIL.**

- [x] **Step 3: Implement**: bar background `#FFFFFF` / `#E7EBE4`; foreground and title `#000000` both themes; unselected `#5A6159` both themes; in the existing native hook, when the platform view is a `Google.Android.Material.BottomNavigation.BottomNavigationView`, set `ItemActiveIndicatorEnabled = true` and `ItemActiveIndicatorColor` to lime. If the Material version in the build has no active indicator, set the selected item's title/icon black and unselected muted only, and say so in the commit message. `PhoneMorePage.cs`: replace the old hex values with the Task 1 token values (via `Application.Current.Resources[...]` like `StaffShell.ThemeColor` did).

- [x] **Step 4: Tests PASS, Android build succeeds.** Check on an emulator or the owner's phone in light and dark (owner checks if no emulator).

- [x] **Step 5: Commit**

```bash
git add src/GymMembership.App/Controls/PhoneNavigation.cs src/GymMembership.App/Views/PhoneMorePage.cs tests/GymMembership.Tests/PhoneNavigationTests.cs
git commit -m "style(android): light bottom bar with black icons and lime selection"
```

---

### Task 6: Page sweep, every page in both themes

**Files:**
- Modify: any of `src/GymMembership.App/Views/*.xaml`, `*.cs`, `src/GymMembership.App/Controls/*` that still carry old colours or flat styling (start from `grep -rnE "#[0-9A-Fa-f]{6}|SetAppThemeColor|LaneView" src/GymMembership.App/Views src/GymMembership.App/Controls src/GymMembership.App/*.cs`)
- Test: `tests/GymMembership.Tests/ContrastTests.cs` (one new fact)

**Interfaces:**
- Consumes: all tokens and styles (Task 1), `IconView` (Task 2).

Rules for each page (no content added or removed):
- Old hex values (`#1F2226`, `#24282C`, `#EDEFF1`, `#ECEEF0`, `#5C646C`, `#9AA3AB`, `#CDD3D9`, `#3A4047`, `#1A1D20`) become the matching token.
- `SetAppThemeColor` calls using `LineLight`/`LineDark` as a selected background become `Lime` (selected) in both themes, with black text.
- `LaneView` decoration stays only where it carries meaning (progress); decorative uses on page headers go.
- Lists of rows inside a `Panel`: rows get `Surface` background and `RoundRectangle 14`.
- Toggle / filter button rows that switch views (e.g. month/week on the coach Dashboard, Today/All lists) use `LimeSmallButton` for the selected one and `SmallOutlineButton` for the rest.
- Overview cards (`AdminOverviewPage.Windows.cs`): the first card (the one that already leads the page) uses `Lime` background; the rest `Card`. No new cards or numbers.

- [x] **Step 1: Test** `No_page_uses_the_old_palette`: no file under `Views` or `Controls` (`*.xaml`, `*.cs`) contains any of the old hex values listed above (case-insensitive).

- [x] **Step 2: Run, expect FAIL** (lists the files).

- [x] **Step 3: Fix each listed file** per the rules.

- [x] **Step 4: Tests PASS; Windows and Android builds succeed.**

- [x] **Step 5: Demo-mode screenshots of every page, light and dark, Windows**, by role:
  - Member: My membership, Packages, Amenities, Book an amenity, Coaches, Messages, Sessions, Time requests, Settings (also as a guest: locked pages).
  - Coach: Dashboard (month and week), Sessions, Time requests, Trainees, Messages, My shifts, My coach profile, Amenities, Settings.
  - Front desk: Home, Payments to verify, Cashier, Check-in, Amenity Status, Messages, Settings.
  - Admin: Overview, Users and roles, Packages and amenities, Discounts, Cancellations & refunds, Void a payment, Gym settings, Revenue, Logs & Archive; bell panel open; one confirmation popup; one error banner.
  Fix anything unreadable or off-style, re-run tests.

- [x] **Step 6: Commit**

```bash
git add <each changed file by path> tests/GymMembership.Tests/ContrastTests.cs
git commit -m "style: every page on the new palette in light and dark"
```

---

### Task 7: Final verification and docs

**Files:**
- Modify: `docs/progress-2026-10-16.md` (one short section: visual redesign, what changed, what the owner still checks on devices)

- [x] **Step 1:** `dotnet test tests/GymMembership.Tests` → 0 failed; both builds succeed.
- [x] **Step 2:** Confirm `supabase.json` is back and no `.bak` exists: `ls src/GymMembership.App/Resources/Raw`.
- [x] **Step 3:** Update the progress doc; commit it by path.
- [ ] **Step 4:** Owner checks Windows and Android on their devices (real project); push and PR only when the owner asks.

---

## Part 3: Sign-in screens (2026-10-31)

> **Status: built**, commit fe9a41a on `visual-redesign-2026-10-30` (pushed, not merged into main). Mockup: `docs/mockups/auth-redesign.html`.

**What changed:** sign in, forgotten password and create account now share one layout (`AuthShell` in `Styles.xaml`). On Windows: the black brand panel on the left with a big glowing dumbbell, the tagline and three things members can do. On phones: a short brand header above the form.

- Sign in: "Welcome back" heading, "Forgot password?" and "New member? Create an account" as text links (no button shape on Windows or Android), a spinner on the button while it works.
- Forgotten password is two steps with a step bar: email, then six code boxes, a 30-second "Resend code" countdown, the new password with a strength meter, and the repeat box.
- Create account: a strength meter and checklist under the password (only "6 to 64 characters" is required; the other two raise the meter), and each field says what is wrong when you leave it.
- Everywhere: a field's frame turns lime (dark) or blue (light) while you type in it, the password eye is a plain grey icon, and Android no longer draws its own underline in text boxes.

**Files:** `Resources/Styles/Styles.xaml` (`AuthShell`, `LinkButton`, `FieldError`), `Views/LoginPage.xaml`, `Views/RegisterPage.xaml(.cs)`, `Controls/Controls.cs` (`FieldFocus`, `CodeBoxes`, `PasswordStrength`), `Controls/DumbbellLogo.cs` (`Bare`), `Controls/Icons.cs` (`Ghost`), `MauiProgram.cs`, `Platforms/Windows/App.xaml`, `GymMembership.Core` (`AccountRules.PasswordStrength`, reset steps and resend countdown in `LoginViewModel`).

- [x] Built for Windows and Android; xUnit 763/763.
- [x] Checked on screen on Windows (dark) and on the Pixel 7 emulator (light and dark).
- [ ] Owner checks a full password reset against the real project (step 2 sends a real email, so it has not been seen yet).
- [ ] Owner checks the phone header with the keyboard open on create account.

**Left out on purpose:** a custom Windows title bar (it would change every signed-in screen), a terms checkbox (there are no terms yet), removing the repeat-password boxes (tests rely on the "don't match" check).

**Android emulator on this PC:** `"C:/Program Files (x86)/Android/android-sdk/emulator/emulator.exe" -avd pixel_7_-_api_36_0`, then `dotnet build src/GymMembership.App -f net10.0-android -t:Run`.
