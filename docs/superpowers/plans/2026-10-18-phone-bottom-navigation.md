# Member and coach phone bottom navigation

## Scope

Android and iOS phones only. Replace the member/coach flyout with five bottom tabs, using the supplied reference for icons above labels and rounded top corners. Windows retains its locked sidebar regardless of window width. Tablets and staff/admin navigation are unchanged. No backend changes.

| Role/platform | Bottom tabs | More destinations |
| --- | --- | --- |
| Member | Membership, Packages, Sessions, Messages, More | Amenities, Coaches, Time requests, Settings |
| Android coach | Overview, Sessions, Trainees, Messages, More | Time requests, My shifts, Attendance, My coach profile, Amenities, Settings |
| iOS coach | Sessions, Requests, Trainees, Messages, More | My shifts, Attendance, My coach profile, Amenities, Settings |

## Implementation

- `Controls/PhoneNavigation.cs` transforms existing role Shell items only for Android/iOS Phone idiom. Other targets return before any navigation changes. Destinations are matched by title rather than numeric position.
- Native MAUI TabBar handles bottom content allocation, platform safe areas, tab selection, and native navigation stacks.
- Locked membership templates are preserved; membership access rechecks remain in place. Ordinary secondary pages use `PhonePageTemplates.ForService` to resolve their registered page and view model instead of requiring a parameterless constructor.
- `Views/PhoneMorePage.cs` shows scrollable, theme-aware, minimum-56-unit buttons. Secondary pages are cached and pushed on the More tab's native back stack. Repeated taps are guarded.
- Existing membership, packages, messages and temporary-password Settings entry points use phone-aware routing. Other shells fall through to original Shell routes. Header behavior is unchanged.
- Android coaches land on the same `CoachOverviewPage` and `CoachOverviewViewModel` as Windows, with narrower Android padding. The existing Windows layout branch is unchanged. Requests moves into More to preserve the five-tab maximum.
- Eight isolated `phone_*.svg` assets provide tab icons.
- Android rounds the tab-bar background, clears its native tint, and colors the exposed rectangular parent using the current page background. This prevents white corner patches in dark mode. Theme changes and navigation reapply the scoped styling.

## Verification — follow-up fixes

- Android Debug build/install: passed, including reinstalling the normal application after testing.
- Windows Debug build: passed.
- Test suite: 531 passed, including three executable template/DI regression tests that reproduce the original constructor failure and verify service-backed creation, plus destination/asset/platform checks.
- Pixel 7 API 36 emulator: all four member More destinations opened and Back returned to More.
- Pixel 7 API 36 emulator, isolated demo application: coach Overview loaded populated summaries; all six coach More destinations opened and Back returned to More.
- Screenshot pixel checks: light-theme corner background = RGB 237/239/241 with white bar; dark-theme corner background = RGB 26/29/32 with bar RGB 36/40/44. No white exposed upper corners in dark mode.
- Coach runtime tests used an isolated `com.slimshape.gymmembership.smoketest` package with the backend configuration excluded through a temporary build targets file; no backend configuration or source files were changed for the test. The smoke-test app was removed afterward.
- Existing photo-picker deprecation and test analyzer warnings remain outside scope.

## Latest UI changes — square bar and Android account menu

These changes supersede the rounded-corner and Android Settings-in-More descriptions above.

- The mobile bottom bar now uses zero corner radius on Android and iOS.
- Android member and coach headers replace the Sign out button with a 48-unit down-chevron button. A native dropdown anchored to it contains Settings and Sign out.
- Settings is filtered out of the Android More menu only. Its internal route remains available for temporary-password flows. iOS retains Settings in More.
- The dropdown uses the existing Settings route and async SignOutCommand. Repeated Settings selections do not stack duplicates, and menu instances are dismissed on unload.
- Windows, iOS, and Android staff/admin headers keep their existing controls and behavior.
- Verification: 535 tests passed; Android and Windows builds passed. Pixel 7 API 36 isolated-demo checks passed for coach/member menu actions, Settings navigation, duplicate prevention, Back to More, and Sign out. Front desk retained the original Sign out button.

## Remaining verification

Physical-device checks, iOS build/runtime checks (requires macOS), guest/payment and temporary-password flows, rotation, narrow screens, larger system text, and message-composer keyboard behavior remain to be verified. Compare Windows visually before release; compile-time branches and Windows regression tests preserve its existing navigation/layout.
