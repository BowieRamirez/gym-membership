# Visual Redesign, Part 2: Page Layouts — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans (owner chose native execution for this redesign). Steps use checkbox (`- [ ]`) syntax.

**Goal:** Lay every page out like the approved reference: wide canvas, cards in a grid with an icon-chip header, big numbers with pill bars, one lime card per page, lists as white rows inside one card. Content, text and behaviour stay exactly as they are.

**Architecture:** Five shared pieces (`CardTitle` control, `Row` / `HeroPanel` / `InkButton` styles, `PageWidth` value, pill-bar `LaneView`) in the existing `Controls/Controls.cs` and `Resources/Styles/Styles.xaml`; then each page's XAML uses them. Card lists that show things (packages, coaches, amenities, payments, sessions) become a two-column `GridItemsLayout` on wide windows; log-like lists (payments history, users, audit, revenue lines) become white rows inside one card.

**Tech Stack:** .NET 10 MAUI XAML, xUnit source checks.

**Spec:** `docs/superpowers/specs/2026-10-30-visual-redesign-design.md`; approved page mockup `.superpowers/brainstorm/1484-1793361048/content/pages-v1.html` (owner, 2026-10-30: "Yes, all pages").

## Global Constraints

- No text, binding, command, page or feature added or removed; only layout and styling. (The mockup's "/ 30 days" and similar totals appear only where the view model already exposes them.)
- Phones keep one column (`OnIdiom Phone=1`).
- Lime cards: every label and button inside is black in both themes (`TextColor="{StaticResource Black}"`, `Style="{StaticResource InkButton}"`/`InkSmallButton`).
- Icons only through `IconView` / `CardTitle`; icon names must exist in `Icons.Data` (arc-free, `[a-z-]` names).
- No `RelativeSource AncestorType` bindings; list pages keep `BasePage.Row<TVm>` handlers.
- Demo-mode checks with `supabase.json` moved aside; restore before every commit; `git add` explicit paths only.
- Commands: `dotnet test tests/GymMembership.Tests` (0 failed), `dotnet build src/GymMembership.App -p:PlatformOnly=windows` and `-p:PlatformOnly=android`.

## Review Focus

1. Dark mode on lime cards: any label or button left with theme text turns light-on-lime and unreadable.
2. Narrow Windows window (~900 px): two-column card grids must still fit; text must wrap, not clip.
3. Empty lists: an empty grid/rows card must still show its existing empty message, not an empty box.
4. Phones: grids drop to one column; nothing set only for Windows breaks Android.
5. Row taps and buttons inside new row/card wrappers still reach their handlers.

---

### Task 1: Shared pieces

**Files:** Modify `src/GymMembership.App/Controls/Controls.cs`, `src/GymMembership.App/Resources/Styles/Styles.xaml`; Test `tests/GymMembership.Tests/IconTests.cs`, new `tests/GymMembership.Tests/PageLayoutTests.cs`.

**Produces:**
- `CardTitle : ContentView` with `Icon` (string), `Text` (string): `IconView` (Size 17, ChipSize 34) + label (BarlowSemiBold, `TextTitle3`-ish 18, theme ink) in a row, spacing 10. `TextColor` (Color?, optional) for lime cards.
- Styles: `Row` (Border: Surface background, `RoundRectangle 14`, no stroke, padding `14,11`); `HeroPanel` (BasedOn `Panel`, background `Lime`); `InkButton` / `InkSmallButton` (black background, white text, both themes); `x:Double PageWidth` = 1240; `SectionTitle` (Label: BarlowMedium 22).
- `LaneView` draws pills: rounded rectangles (width ≈ 2.2 × height... height = control height, radius = width/2), filled ink up to `Fraction`, dashed outline after; existing bindable properties unchanged.

- [ ] Tests: `IconTests.Pages` widened to every `Views/*.xaml`; `PageLayoutTests.No_page_keeps_the_old_narrow_column` (no `MaximumWidthRequest="920"` in Views), `Lane_is_drawn_as_pills` (Controls.cs contains `FillRoundedRectangle` in `LaneDrawable`), `Card_title_is_shared` (Controls.cs has `class CardTitle`, Styles.xaml has `x:Key="Row"`, `x:Key="HeroPanel"`, `x:Key="InkButton"`). Run: FAIL.
- [ ] Implement the pieces. Run tests: the narrow-column test still fails until Tasks 2-4; the others PASS.
- [ ] Commit.

### Task 2: Member pages
My membership (hero lime days-left card with pill lane; QR, pay card beside it in a 3-column grid on wide windows; terms as 2-column cards with `CardTitle`; payments as `Row`s in one Panel), Packages (grid of package cards, price as numeral), Amenities (grid), Book an amenity (cards around the day grid and bookings), Coaches (grid of coach cards), Sessions (rows in one card), Time requests (form card + rows), Messages (thread list in a card), Settings (sections as cards in a 2-column grid), locked page. Screenshots dark + light. Commit.

### Task 3: Coach pages
Dashboard (stat tiles as cards with `CardTitle`, first lime; calendar in a card), Trainees (grid), My shifts (form card + rows), My coach profile (cards), Amenities. Screenshots. Commit.

### Task 4: Front desk and admin pages
Home (4 tile cards, first lime, `CardTitle`), Payments to verify (grid), Cashier (choice cards + order card), Check-in (coach rows, member rows in cards), Amenity Status (grid), Overview (cards with `CardTitle`), Users and roles (add-user card with fields in a 4-column grid; accounts as rows with avatar letters already shown? only what exists), Packages and amenities, Discounts, Cancellations & refunds, Void a payment (grid), Gym settings, Revenue (total as lime hero card; lines as rows), Logs & Archive (rows in a card). Screenshots. Commit.

### Task 5: Verify and review
Full tests, both builds, demo walkthrough of every page in both themes, fresh reviewer on the range, ponytail review, progress doc note. Commit.
